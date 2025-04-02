using System.Diagnostics;
using System.Text;

using Choir.FrontEnd.C.Diagnostics;
using Choir.Source;

namespace Choir.FrontEnd.C.Preprocess;

public sealed class CLexer
{
    [Flags]
    private enum LexerState
    {
        None = 0,
        WithinDirective,
        HasHeaderNames,
    }

    public static List<CToken> ReadTokens(CCContext context, SourceText source)
    {
        var lexer = new CLexer(context, source);
        var tokens = new List<CToken>();

        while (true)
        {
            lexer.ReadManyTokensInto(tokens);
            if (tokens[^1].Kind is CTokenKind.EndOfFile)
                break;
        }

        Debug.Assert(tokens.Count == 0 || tokens[^1].Kind == CTokenKind.EndOfFile);

        context.Diag.Flush();
        return tokens;
    }

    private readonly CCContext _context;
    private readonly SourceText _source;

    private int _readPosition;
    private bool _isAtStartOfLine = true;

    private bool IsAtEnd => _readPosition >= _source.Text.Length;
    private char CurrentCharacter => PeekCharacterAndStride(0, LexerState.None, out int _);
    private SourceLocation CurrentLocation => new(_readPosition);

    private CLexer(CCContext context, SourceText source)
    {
        _context = context;
        _source = source;
    }

    private void Advance(int amount = 1) => Advance(amount, LexerState.None);
    private void Advance(LexerState state) => Advance(1, state);
    private void Advance(int amount, LexerState state)
    {
        _context.Assert(amount >= 1, $"Parameter {nameof(amount)} to function {nameof(CLexer)}::{nameof(Advance)} must be positive; advancing the lexer must always move forward at least one character if possible.");
        
        for (int i = 0; i < amount && !IsAtEnd; i++)
        {
            char c = PeekCharacterAndStride(0, state, out int stride);
            if (c is '\n') _isAtStartOfLine = true;
            _readPosition += stride;
        }

        _readPosition = Math.Min(_readPosition, _source.Text.Length);
    }

    private bool TryAdvance(char c, LexerState state = LexerState.None)
    {
        if (CurrentCharacter != c)
            return false;

        Advance(state);
        return true;
    }

    private char PeekCharacter(int ahead, LexerState state = LexerState.None) => PeekCharacterAndStride(ahead, state, out int _);
    private char PeekCharacterAndStride(int ahead, LexerState state, out int stride)
    {
        _context.Assert(ahead >= 0, $"Parameter {nameof(ahead)} to function {nameof(CLexer)}::{nameof(PeekCharacterAndStride)} must be non-negative; the lexer should never rely on character look-back.");

        int internalOffset = 0;
        for (int i = 0; i < ahead; i++)
        {
            char _ = PeekCharacterInternal(internalOffset, out int advance);
            internalOffset += advance;
        }

        return PeekCharacterInternal(internalOffset, out stride);

        char PeekCharacterInternal(int at, out int stride)
        {
            stride = 1;
            char c = SafePeekSingleChar(at);

            switch (c)
            {
                case '\\' when SafePeekSingleChar(at + 1) is '\n' && SafePeekSingleChar(at + 2) == '\r':
                case '\\' when SafePeekSingleChar(at + 1) is '\r' && SafePeekSingleChar(at + 2) == '\n':
                {
                    stride = 4;
                    return SafePeekSingleChar(at + 3);
                }
                
                case '\\' when SafePeekSingleChar(at + 1) is '\n' or '\r':
                {
                    stride = 3;
                    return SafePeekSingleChar(at + 2);
                }

                case '\n' when SafePeekSingleChar(at + 1) is '\r':
                case '\r' when SafePeekSingleChar(at + 1) is '\n':
                {
                    stride = 2;
                    return '\n';
                }

                default: return c;
            }
        }

        char SafePeekSingleChar(int at)
        {
            int peekIndex = _readPosition + at;
            if (peekIndex >= _source.Text.Length)
                return '\0';

            return _source.Text[peekIndex];
        }
    }

    private SourceRange GetRange(SourceLocation beginLocation) => new(beginLocation, CurrentLocation);

    private CTriviaList ReadTrivia(bool isLeading, LexerState state)
    {
        if (IsAtEnd) return new([], isLeading);

        var trivia = new List<CTrivia>(2);
        while (!IsAtEnd)
        {
            var beginLocation = CurrentLocation;
            switch (CurrentCharacter)
            {
                // the character peeker automatically transforms all forms of newline to just \n, so this one case should grab all valid newlines the lexer recognizes.
                case '\n' when !state.HasFlag(LexerState.WithinDirective):
                {
                    Advance();
                    trivia.Add(new CTriviaNewLine(GetRange(beginLocation)));
                    // Trailing trivia always ends with a newline if encountered.
                    if (!isLeading) return new(trivia, isLeading);
                } break;

                case ' ' or '\t' or '\v':
                {
                    Advance();
                    while (!IsAtEnd && CurrentCharacter is ' ' or '\t' or '\v')
                        Advance();
                    trivia.Add(new CTriviaWhiteSpace(GetRange(beginLocation)));
                } break;

                case '/' when PeekCharacter(1) == '/':
                {
                    Advance(2);
                    while (!IsAtEnd && CurrentCharacter is not '\n')
                        Advance();
                    trivia.Add(new CTriviaLineComment(GetRange(beginLocation)));
                } break;

                case '/' when PeekCharacter(1) == '*':
                {
                    Advance(2);

                    bool isClosed = false;
                    while (!isClosed && !IsAtEnd)
                    {
                        if (CurrentCharacter == '*' && PeekCharacter(1) == '/')
                        {
                            Advance(2);
                            isClosed = true;
                            break;
                        }
                        else Advance();
                    }

                    if (!isClosed)
                        _context.ErrorUnclosedComment(_source, beginLocation);

                    trivia.Add(new CTriviaDelimitedComment(GetRange(beginLocation)));
                } break;

                // A shebang, `#!`, at the very start of the file is treated as a line comment.
                // This allows running C files as scripts on Unix systems without also making `#` or `#!` line comment sequences anywhere else.
                case '#' when _readPosition == 0 && PeekCharacter(1) == '!':
                {
                    Advance(2);
                    while (!IsAtEnd && CurrentCharacter is not ('\r' or '\n'))
                        Advance();
                    trivia.Add(new CTriviaShebangComment(GetRange(beginLocation)));
                } break;

                // when nothing matches, there is no trivia to read; simply return what we currently have.
                default: return new(trivia, isLeading);
            }

            _context.Assert(_readPosition > beginLocation.Offset, _source, beginLocation, $"{nameof(CLexer)}::{nameof(ReadTokenNoPreprocess)} failed to consume any non-trivia characters from the source text and did not return the current list of trivia if required.");
        }

        // end of file broke us out of the loop; simply return what we read.
        return new(trivia, isLeading);
    }

    private void ReadManyTokensInto(List<CToken> tokens)
    {
        var firstToken = ReadTokenNoPreprocess();
        tokens.Add(firstToken);

        if (firstToken is { Kind: CTokenKind.Pound, IsAtStartOfLine: true })
        {
            var secondToken = ReadTokenNoPreprocess();
            tokens.Add(secondToken);

            switch (secondToken)
            {
                case { Kind: CTokenKind.Identifier } when secondToken.StringValue == "include":
                {
                    ReadDirectiveTokensInto(tokens, LexerState.HasHeaderNames);
                } break;

                default:
                {
                    ReadDirectiveTokensInto(tokens);
                } break;
            }
        }
    }

    private CToken ReadTokenNoPreprocess(LexerState state = LexerState.None)
    {
        var leadingTrivia = ReadTrivia(isLeading: true, state);
        var beginLocation = CurrentLocation;

        // once we start lexing a token, it's the only token that's considered at the start of its line.
        // store the current state of the start-of-line flag, and set it to false for all subsequent tokens.
        // the trivia lexer will reset it to true when a "real" newline is encountered.
        bool isAtStartOfLine = _isAtStartOfLine;
        _isAtStartOfLine = false;

        if (IsAtEnd)
        {
            return new(CTokenKind.EndOfFile, _source, GetRange(beginLocation), leadingTrivia, new([], false))
            {
                // probably doesn't matter, but hey let's track it anyway.
                IsAtStartOfLine = isAtStartOfLine,
            };
        }
        
        StringView stringValue = default;

        var tokenKind = CTokenKind.Invalid;
        switch (CurrentCharacter)
        {
            case '\n' when state.HasFlag(LexerState.WithinDirective):
            {
                Advance();
                tokenKind = CTokenKind.DirectiveEnd;
            } break;

            case '<' when state.HasFlag(LexerState.HasHeaderNames):
            {
                var tokenTextBuilder = new StringBuilder();
                tokenKind = CTokenKind.HeaderName;

                Advance();
                while (!IsAtEnd && CurrentCharacter is not '>')
                {
                    tokenTextBuilder.Append(CurrentCharacter);
                    Advance();
                }

                if (!TryAdvance('>'))
                    _context.ErrorExpectedMatchingCloseDelimiter(_source, '<', '>', CurrentLocation, beginLocation);

                stringValue = tokenTextBuilder.ToString();
            } break;

            case '(': Advance(); tokenKind = CTokenKind.OpenParen; break;
            case ')': Advance(); tokenKind = CTokenKind.CloseParen; break;
            case '[': Advance(); tokenKind = CTokenKind.OpenSquare; break;
            case ']': Advance(); tokenKind = CTokenKind.CloseSquare; break;
            case '{': Advance(); tokenKind = CTokenKind.OpenCurly; break;
            case '}': Advance(); tokenKind = CTokenKind.CloseCurly; break;
            case ';': Advance(); tokenKind = CTokenKind.SemiColon; break;
            case '?': Advance(); tokenKind = CTokenKind.Question; break;
            case '~': Advance(); tokenKind = CTokenKind.Tilde; break;

            case '!':
            {
                Advance();
                if (TryAdvance('='))
                    tokenKind = CTokenKind.BangEqual;
                else tokenKind = CTokenKind.Bang;
            } break;

            case '#':
            {
                Advance();
                if (TryAdvance('#'))
                    tokenKind = CTokenKind.PoundPound;
                else tokenKind = CTokenKind.Pound;
            } break;

            case '%':
            {
                Advance();
                if (TryAdvance('='))
                    tokenKind = CTokenKind.PercentEqual;
                else tokenKind = CTokenKind.Percent;
            } break;

            case '&':
            {
                Advance();
                if (TryAdvance('&'))
                    tokenKind = CTokenKind.AmpersandAmpersand;
                else if (TryAdvance('='))
                    tokenKind = CTokenKind.AmpersandEqual;
                else tokenKind = CTokenKind.Ampersand;
            } break;

            case '*':
            {
                Advance();
                if (TryAdvance('='))
                    tokenKind = CTokenKind.StarEqual;
                else tokenKind = CTokenKind.Star;
            } break;

            case '+':
            {
                Advance();
                if (TryAdvance('+'))
                    tokenKind = CTokenKind.PlusPlus;
                else if (TryAdvance('='))
                    tokenKind = CTokenKind.PlusEqual;
                else tokenKind = CTokenKind.Plus;
            } break;

            case '-':
            {
                Advance();
                if (TryAdvance('-'))
                    tokenKind = CTokenKind.MinusMinus;
                else if (TryAdvance('='))
                    tokenKind = CTokenKind.MinusEqual;
                else if (TryAdvance('>'))
                    tokenKind = CTokenKind.MinusGreater;
                else tokenKind = CTokenKind.Minus;
            } break;

            case '.':
            {
                Advance();
                if (CurrentCharacter is '.' && PeekCharacter(1) is '.')
                {
                    Advance(2);
                    tokenKind = CTokenKind.DotDotDot;
                }
                else tokenKind = CTokenKind.Dot;
            } break;

            case '/':
            {
                Advance();
                if (TryAdvance('='))
                    tokenKind = CTokenKind.SlashEqual;
                else tokenKind = CTokenKind.Slash;
            } break;

            case ':':
            {
                Advance();
                if (TryAdvance(':'))
                    tokenKind = CTokenKind.ColonColon;
                else tokenKind = CTokenKind.Colon;
            } break;

            case '<':
            {
                Advance();
                if (TryAdvance('='))
                    tokenKind = CTokenKind.LessEqual;
                else if (TryAdvance('<'))
                {
                    if (TryAdvance('='))
                        tokenKind = CTokenKind.LessLessEqual;
                    else tokenKind = CTokenKind.LessLess;
                }
                else tokenKind = CTokenKind.Less;
            } break;

            case '=':
            {
                Advance();
                if (TryAdvance('='))
                    tokenKind = CTokenKind.EqualEqual;
                else tokenKind = CTokenKind.Equal;
            } break;

            case '>':
            {
                Advance();
                if (TryAdvance('='))
                    tokenKind = CTokenKind.GreaterEqual;
                else if (TryAdvance('>'))
                {
                    if (TryAdvance('='))
                        tokenKind = CTokenKind.GreaterGreaterEqual;
                    else tokenKind = CTokenKind.GreaterGreater;
                }
                else tokenKind = CTokenKind.Greater;
            } break;

            case '^':
            {
                Advance();
                if (TryAdvance('='))
                    tokenKind = CTokenKind.CaretEqual;
                else tokenKind = CTokenKind.Caret;
            } break;

            case '|':
            {
                Advance();
                if (TryAdvance('|'))
                    tokenKind = CTokenKind.PipePipe;
                else if (TryAdvance('='))
                    tokenKind = CTokenKind.PipeEqual;
                else tokenKind = CTokenKind.Pipe;
            } break;

            case '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'):
            {
                var tokenTextBuilder = new StringBuilder();
                tokenTextBuilder.Append(CurrentCharacter);

                tokenKind = CTokenKind.Identifier;

                Advance();
                while (CurrentCharacter is '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                {
                    tokenTextBuilder.Append(CurrentCharacter);
                    Advance();
                }

                stringValue = tokenTextBuilder.ToString();
            } break;

            default:
            {
                _context.ErrorUnexpectedCharacter(_source, beginLocation);
                tokenKind = CTokenKind.UnexpectedCharacter;
                Advance();
            } break;
        }

        var tokenRange = GetRange(beginLocation);
        _context.Assert(_readPosition > beginLocation.Offset, _source, beginLocation, $"{nameof(CLexer)}::{nameof(ReadTokenNoPreprocess)} failed to consume any non-trivia characters from the source text and did not return an EOF token.");
        _context.Assert(tokenKind != CTokenKind.Invalid, _source, beginLocation, $"{nameof(CLexer)}::{nameof(ReadTokenNoPreprocess)} failed to assign a non-invalid kind to the read token.");

        var trailingTrivia = ReadTrivia(isLeading: false, state);
        return new(tokenKind, _source, tokenRange, leadingTrivia, trailingTrivia)
        {
            StringValue = stringValue,
            IsAtStartOfLine = isAtStartOfLine,
        };
    }

    private void ReadDirectiveTokensInto(List<CToken> tokens, LexerState directiveState = LexerState.None)
    {
        directiveState |= LexerState.WithinDirective;
        while (!IsAtEnd && ReadTokenNoPreprocess(directiveState) is { } token)
        {
            tokens.Add(token);
            if (token.Kind is CTokenKind.DirectiveEnd)
                break;
        }
    }
}
