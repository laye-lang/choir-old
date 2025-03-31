using System.Numerics;

using Choir.FrontEnd.Score.Diagnostics;
using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public sealed class ScoreLexer
{
    public static List<ScoreSyntaxToken> ReadTokens(ScoreContext context, SourceText source)
    {
        var lexer = new ScoreLexer(context, source);
        var tokens = new List<ScoreSyntaxToken>();

        while (true)
        {
            var token = lexer.ReadToken();
            tokens.Add(token);

            if (token.Kind == ScoreTokenKind.EndOfFile)
                break;
        }

        context.Diag.Flush();
        return tokens;
    }

    private static readonly Dictionary<StringView, ScoreTokenKind> _keywords = new()
    {
        { "abstract", ScoreTokenKind.Abstract },
        { "alias", ScoreTokenKind.Alias },
        { "alignof", ScoreTokenKind.Alignof },
        { "and", ScoreTokenKind.And },
        { "base", ScoreTokenKind.Base },
        { "bool", ScoreTokenKind.Bool },
        { "break", ScoreTokenKind.Break },
        { "case", ScoreTokenKind.Case },
        { "const", ScoreTokenKind.Const },
        { "continue", ScoreTokenKind.Continue },
        { "countof", ScoreTokenKind.Countof },
        { "default", ScoreTokenKind.Default },
        { "defer", ScoreTokenKind.Defer },
        { "delete", ScoreTokenKind.Delete },
        { "do", ScoreTokenKind.Do },
        { "else", ScoreTokenKind.Else },
        { "enum", ScoreTokenKind.Enum },
        { "export", ScoreTokenKind.Export },
        { "extern", ScoreTokenKind.Extern },
        { "false", ScoreTokenKind.False },
        { "floatsized", ScoreTokenKind.FloatSized },
        { "for", ScoreTokenKind.For },
        { "func", ScoreTokenKind.Func },
        { "goto", ScoreTokenKind.Goto },
        { "if", ScoreTokenKind.If },
        { "import", ScoreTokenKind.Import },
        { "int", ScoreTokenKind.Int },
        { "internal", ScoreTokenKind.Internal },
        { "intsized", ScoreTokenKind.IntSized },
        { "is", ScoreTokenKind.Is },
        { "let", ScoreTokenKind.Let },
        { "module", ScoreTokenKind.Module },
        { "new", ScoreTokenKind.New },
        { "nil", ScoreTokenKind.Nil },
        { "noreturn", ScoreTokenKind.Noreturn },
        { "not", ScoreTokenKind.Not },
        { "offsetof", ScoreTokenKind.Offsetof },
        { "operator", ScoreTokenKind.Operator },
        { "or", ScoreTokenKind.Or },
        { "override", ScoreTokenKind.Override },
        { "private", ScoreTokenKind.Private },
        { "protected", ScoreTokenKind.Protected },
        { "public", ScoreTokenKind.Public },
        { "rankof", ScoreTokenKind.Rankof },
        { "readonly", ScoreTokenKind.Readonly },
        { "ref", ScoreTokenKind.Ref },
        { "return", ScoreTokenKind.Return },
        { "sealed", ScoreTokenKind.Sealed },
        { "sizeof", ScoreTokenKind.Sizeof },
        { "static", ScoreTokenKind.Static },
        { "struct", ScoreTokenKind.Struct },
        { "switch", ScoreTokenKind.Switch },
        { "this", ScoreTokenKind.This },
        { "trait", ScoreTokenKind.Trait },
        { "true", ScoreTokenKind.True },
        { "typeof", ScoreTokenKind.Typeof },
        { "union", ScoreTokenKind.Union },
        { "varargs", ScoreTokenKind.Varargs },
        { "variant", ScoreTokenKind.Variant },
        { "virtual", ScoreTokenKind.Virtual },
        { "void", ScoreTokenKind.Void },
        { "while", ScoreTokenKind.While },
        { "writeonly", ScoreTokenKind.Writeonly },
        { "yield", ScoreTokenKind.Yield },
    };

    private readonly ScoreContext _context;
    private readonly SourceText _source;

    private int _readPosition;

    private bool IsAtEnd => _readPosition >= _source.Text.Length;
    private char CurrentCharacter => PeekCharacter();
    private SourceLocation CurrentLocation => new(_readPosition);

    private ScoreLexer(ScoreContext context, SourceText source)
    {
        _context = context;
        _source = source;
    }

    private void Advance(int amount = 1)
    {
        _context.Assert(amount >= 1, $"Parameter {nameof(amount)} to function {nameof(ScoreLexer)}::{nameof(Advance)} must be positive; advancing the lexer must always move forward at least one character if possible.");
        if (IsAtEnd) return;
        _readPosition = Math.Min(_readPosition + amount, _source.Text.Length);
    }

    private bool TryAdvance(char c)
    {
        if (CurrentCharacter != c)
            return false;

        Advance();
        return true;
    }

    private char PeekCharacter(int ahead = 0)
    {
        _context.Assert(ahead >= 0, $"Parameter {nameof(ahead)} to function {nameof(ScoreLexer)}::{nameof(PeekCharacter)} must be non-negative; the lexer should never rely on character look-back.");

        int peekIndex = _readPosition + ahead;
        if (peekIndex >= _source.Text.Length)
            return '\0';

        return _source.Text[peekIndex];
    }

    private SourceRange GetRange(SourceLocation beginLocation) => new(beginLocation, CurrentLocation);

    private ScoreTriviaList ReadTrivia(bool isLeading)
    {
        if (IsAtEnd) return new([], isLeading);

        var trivia = new List<ScoreTrivia>(2);
        while (!IsAtEnd)
        {
            var beginLocation = CurrentLocation;
            switch (CurrentCharacter)
            {
                // both \r\n and \n\r pairings should be treated as a single newline trivia.
                case '\r' when PeekCharacter(1) == '\n':
                case '\n' when PeekCharacter(1) == '\r':
                {
                    Advance(2);
                    trivia.Add(new ScoreTriviaNewLine(GetRange(beginLocation)));
                    // Trailing trivia always ends with a newline if encountered.
                    if (!isLeading) return new(trivia, isLeading);
                } break;

                // otherwise, both lone \r and lone \n are also single newline trivia.
                case '\r' or '\n':
                {
                    Advance();
                    trivia.Add(new ScoreTriviaNewLine(GetRange(beginLocation)));
                    // Trailing trivia always ends with a newline if encountered.
                    if (!isLeading) return new(trivia, isLeading);
                } break;

                case ' ' or '\t' or '\v':
                {
                    Advance();
                    while (!IsAtEnd && CurrentCharacter is ' ' or '\t' or '\v')
                        Advance();
                    trivia.Add(new ScoreTriviaWhiteSpace(GetRange(beginLocation)));
                } break;

                case '/' when PeekCharacter(1) == '/':
                {
                    Advance(2);
                    while (!IsAtEnd && CurrentCharacter is not ('\r' or '\n'))
                        Advance();
                    trivia.Add(new ScoreTriviaLineComment(GetRange(beginLocation)));
                } break;

                case '/' when PeekCharacter(1) == '*':
                {
                    Advance(2);

                    int depth = 1;
                    while (depth > 0 && !IsAtEnd)
                    {
                        if (CurrentCharacter == '*' && PeekCharacter(1) == '/')
                        {
                            Advance(2);
                            depth--;
                        }
                        else if (CurrentCharacter == '/' && PeekCharacter(1) == '*')
                        {
                            Advance(2);
                            depth++;
                        }
                        else Advance();
                    }

                    if (depth > 0)
                        _context.ErrorUnclosedComment(_source, beginLocation);

                    trivia.Add(new ScoreTriviaDelimitedComment(GetRange(beginLocation)));
                } break;

                // A shebang, `#!`, at the very start of the file is treated as a line comment.
                // This allows running Score files as scripts on Unix systems without also making `#` or `#!` line comment sequences anywhere else.
                case '#' when _readPosition == 0 && PeekCharacter(1) == '!':
                {
                    Advance(2);
                    while (!IsAtEnd && CurrentCharacter is not ('\r' or '\n'))
                        Advance();
                    trivia.Add(new ScoreTriviaShebangComment(GetRange(beginLocation)));
                } break;

                // when nothing matches, there is no trivia to read; simply return what we currently have.
                default: return new(trivia, isLeading);
            }

            _context.Assert(_readPosition > beginLocation.Offset, _source, beginLocation, $"{nameof(ScoreLexer)}::{nameof(ReadToken)} failed to consume any non-trivia characters from the source text and did not return the current list of trivia if required.");
        }

        // end of file broke us out of the loop; simply return what we read.
        return new(trivia, isLeading);
    }

    private ScoreSyntaxToken ReadToken()
    {
        var leadingTrivia = ReadTrivia(isLeading: true);
        var beginLocation = CurrentLocation;

        if (IsAtEnd)
            return new(ScoreTokenKind.EndOfFile, GetRange(beginLocation), leadingTrivia, new([], false));

        ReadOnlyMemory<char> stringValue = default;
        BigInteger integerValue = default;
        double floatValue = 0;

        var tokenKind = ScoreTokenKind.Invalid;
        switch (CurrentCharacter)
        {
            case '(': tokenKind = ScoreTokenKind.OpenParen; Advance(); break;
            case ')': tokenKind = ScoreTokenKind.CloseParen; Advance(); break;
            case '[': tokenKind = ScoreTokenKind.OpenSquare; Advance(); break;
            case ']': tokenKind = ScoreTokenKind.CloseSquare; Advance(); break;
            case '{': tokenKind = ScoreTokenKind.OpenCurly; Advance(); break;
            case '}': tokenKind = ScoreTokenKind.CloseCurly; Advance(); break;

            case ',': tokenKind = ScoreTokenKind.Comma; Advance(); break;
            case ';': tokenKind = ScoreTokenKind.SemiColon; Advance(); break;

            case ':':
            {
                Advance();
                tokenKind
                    = TryAdvance(':') ? ScoreTokenKind.ColonColon
                    : ScoreTokenKind.Colon;
            } break;

            case '+':
            {
                Advance();
                tokenKind
                    = TryAdvance('=') ? ScoreTokenKind.PlusEqual
                    : ScoreTokenKind.Plus;
            } break;

            case '-':
            {
                Advance();
                tokenKind
                    = TryAdvance('=') ? ScoreTokenKind.MinusEqual
                    : TryAdvance('>') ? ScoreTokenKind.MinusGreater
                    : ScoreTokenKind.Minus;
            } break;

            case '*':
            {
                Advance();
                tokenKind
                    = TryAdvance('=') ? ScoreTokenKind.StarEqual
                    : ScoreTokenKind.Star;
            } break;

            case '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'):
            {
                tokenKind = ScoreTokenKind.Identifier;

                Advance();
                while (CurrentCharacter is '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                    Advance();

                if (ScoreSyntaxFacts.CanContinueIdentifier(CurrentCharacter))
                {
                    Advance();
                    ContinueLexIdentifierSlow(beginLocation);
                }
                else
                {
                    var tokenStringValue = _source.Slice(beginLocation, CurrentLocation);
                    if (_keywords.TryGetValue(tokenStringValue, out var keywordKind))
                        tokenKind = keywordKind;
                    else if (tokenStringValue.StartsWith("int") && IsSubstringOnlyDigits(tokenStringValue[3..]))
                        tokenKind = ScoreTokenKind.IntSized;
                    else if (tokenStringValue.StartsWith("float") && IsSubstringOnlyDigits(tokenStringValue[5..]))
                        tokenKind = ScoreTokenKind.FloatSized;
                    else stringValue = tokenStringValue;

                    static bool IsSubstringOnlyDigits(ReadOnlySpan<char> s)
                    {
                        foreach (char c in s)
                        {
                            if (c is not (>= '0' and <= '9'))
                                return false;
                        }

                        return true;
                    }
                }
            } break;

            case >= '0' and <= '9':
            {
                tokenKind = ScoreTokenKind.LiteralInteger;
                while (CurrentCharacter is >= '0' and <= '9')
                    Advance();
                
                if (ScoreSyntaxFacts.CanContinueIdentifier(CurrentCharacter))
                {
                    _context.ErrorInvalidCharacterInNumberLiteral(_source, CurrentLocation);
                    while (ScoreSyntaxFacts.CanContinueIdentifier(CurrentCharacter))
                        Advance();
                }
                else integerValue = BigInteger.Parse(_source.Slice(beginLocation, CurrentLocation).Span);
            } break;

            default:
            {
                if (ScoreSyntaxFacts.CanStartIdentifier(CurrentCharacter))
                {
                    Advance();
                    ContinueLexIdentifierSlow(beginLocation);
                }
                else
                {
                    _context.ErrorUnexpectedCharacter(_source, beginLocation);
                    tokenKind = ScoreTokenKind.UnexpectedCharacter;
                    Advance();
                }
            } break;
        }

        void ContinueLexIdentifierSlow(SourceLocation identifierBeginLocation)
        {
            while (ScoreSyntaxFacts.CanContinueIdentifier(CurrentCharacter))
                Advance();

            tokenKind = ScoreTokenKind.Identifier;
            stringValue = _source.Slice(beginLocation, CurrentLocation);

            // the slow path will never produce a language keyword, so we don't check.
            // all language keywords use characters found exclusively in the fast path.
        }

        var tokenRange = GetRange(beginLocation);
        _context.Assert(_readPosition > beginLocation.Offset, _source, beginLocation, $"{nameof(ScoreLexer)}::{nameof(ReadToken)} failed to consume any non-trivia characters from the source text and did not return an EOF token.");
        _context.Assert(tokenKind != ScoreTokenKind.Invalid, _source, beginLocation, $"{nameof(ScoreLexer)}::{nameof(ReadToken)} failed to assign a non-invalid kind to the read token.");

        var trailingTrivia = ReadTrivia(isLeading: false);
        return new(tokenKind, tokenRange, leadingTrivia, trailingTrivia)
        {
            StringValue = stringValue,
            IntegerValue = integerValue,
            FloatValue = floatValue,
        };
    }
}
