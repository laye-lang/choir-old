using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace Choir.Front.Laye.Syntax;

public sealed class Lexer(SourceFile sourceFile)
{
    private static readonly Dictionary<string, TokenKind> _keywordTokensKinds = new()
    {
        {"global", TokenKind.Global},
        {"var", TokenKind.Var},
        {"void", TokenKind.Void},
        {"noreturn", TokenKind.NoReturn},
        {"bool", TokenKind.Bool},
        {"int", TokenKind.Int},
        {"__laye_ffi_bool", TokenKind.BuiltinFFIBool},
        {"__laye_ffi_char", TokenKind.BuiltinFFIChar},
        {"__laye_ffi_short", TokenKind.BuiltinFFIShort},
        {"__laye_ffi_int", TokenKind.BuiltinFFIInt},
        {"__laye_ffi_long", TokenKind.BuiltinFFILong},
        {"__laye_ffi_longlong", TokenKind.BuiltinFFILongLong},
        {"__laye_ffi_float", TokenKind.BuiltinFFIFloat},
        {"__laye_ffi_double", TokenKind.BuiltinFFIDouble},
        {"__laye_ffi_longdouble", TokenKind.BuiltinFFILongDouble},
        {"true", TokenKind.True},
        {"false", TokenKind.False},
        {"nil", TokenKind.Nil},
        {"if", TokenKind.If},
        {"else", TokenKind.Else},
        {"for", TokenKind.For},
        {"while", TokenKind.While},
        {"do", TokenKind.Do},
        {"switch", TokenKind.Switch},
        {"case", TokenKind.Case},
        {"default", TokenKind.Default},
        {"return", TokenKind.Return},
        {"break", TokenKind.Break},
        {"continue", TokenKind.Continue},
        {"fallthrough", TokenKind.Fallthrough},
        {"yield", TokenKind.Yield},
        {"unreachable", TokenKind.Unreachable},
        {"defer", TokenKind.Defer},
        {"discard", TokenKind.Discard},
        {"goto", TokenKind.Goto},
        {"xyzzy", TokenKind.Xyzzy},
        {"assert", TokenKind.Assert},
        {"try", TokenKind.Try},
        {"catch", TokenKind.Catch},
        {"struct", TokenKind.Struct},
        {"variant", TokenKind.Variant},
        {"enum", TokenKind.Enum},
        {"alias", TokenKind.Alias},
        {"delegate", TokenKind.Delegate},
        {"register", TokenKind.Register},
        {"template", TokenKind.Template},
        {"module", TokenKind.Module},
        {"test", TokenKind.Test},
        {"import", TokenKind.Import},
        {"export", TokenKind.Export},
        {"operator", TokenKind.Operator},
        {"mut", TokenKind.Mut},
        {"ref", TokenKind.Ref},
        {"new", TokenKind.New},
        {"delete", TokenKind.Delete},
        {"cast", TokenKind.Cast},
        {"eval", TokenKind.Eval},
        {"is", TokenKind.Is},
        {"sizeof", TokenKind.Sizeof},
        {"alignof", TokenKind.Alignof},
        {"offsetof", TokenKind.Offsetof},
        {"countof", TokenKind.Countof},
        {"rankof", TokenKind.Rankof},
        {"typeof", TokenKind.Typeof},
        {"not", TokenKind.Not},
        {"and", TokenKind.And},
        {"or", TokenKind.Or},
        {"xor", TokenKind.Xor},
        {"varargs", TokenKind.Varargs},
        {"const", TokenKind.Const},
        {"foreign", TokenKind.Foreign},
        {"inline", TokenKind.Inline},
        {"callconv", TokenKind.Callconv},
        {"pure", TokenKind.Pure},
        {"discardable", TokenKind.Discardable},
    };

    public SourceFile SourceFile { get; } = sourceFile;
    public ChoirContext Context { get; } = sourceFile.Context;

    private readonly int _fileId = sourceFile.FileId;
    private readonly string _text = sourceFile.Text;
    private readonly int _textLength = sourceFile.Text.Length;

    private struct TokenInfo
    {
        public TokenKind Kind;
        public int Position;
        public int Length;
        public string TextValue = "";
        public BigInteger IntegerValue = BigInteger.Zero;

        public TokenInfo()
        {
        }
    }

    private enum ScanNumberFlags
    {
        NotNumber,
        VanillaInteger,
        RadixInteger,
        VanillaFloat,
        RadixFloat,
    }

    private class LexerResetPoint(Lexer lexer) : IDisposable
    {
        private readonly Lexer _lexer = lexer;
        private readonly int _position = lexer._position;

        public void Dispose()
        {
            _lexer._position = _position;
        }
    }

    private int _position;
    private readonly StringBuilder _stringBuilder = new();

    private bool IsAtEnd => _position >= _textLength;
    private char CurrentCharacter => Peek(0);
    private Location CurrentLocation => new(_position, 1, _fileId);

    private char Peek(int ahead)
    {
        Context.Assert(ahead >= 0, $"peeking ahead in the lexer should only ever look forward or at the current character. the caller requested {ahead} characters ahead, which is illegal.");

        int peekPosition = _position + ahead;
        if (peekPosition >= _textLength)
            return '\0';

        return _text[peekPosition];
    }

    private void Advance()
    {
        _position++;
    }

    private bool TryAdvance(char c)
    {
        if (CurrentCharacter != c)
            return false;

        Advance();
        return true;
    }

    public SyntaxToken ReadToken()
    {
        TokenInfo tokenInfo = default;
        ReadTokenInfo(ref tokenInfo);
        return new SyntaxToken(tokenInfo.Kind, new Location(tokenInfo.Position, tokenInfo.Length, _fileId))
        {
            TextValue = tokenInfo.TextValue,
            IntegerValue = tokenInfo.IntegerValue,
        };
    }

    private void ReadTokenInfo(ref TokenInfo tokenInfo)
    {
        SkipTrivia();

        tokenInfo.Kind = TokenKind.Invalid;
        tokenInfo.Position = _position;

        if (IsAtEnd)
            tokenInfo.Kind = TokenKind.EndOfFile;
        else switch (CurrentCharacter)
        {
            case '_':
            case (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'):
            {
                ReadIdentifier(ref tokenInfo);
                if (_keywordTokensKinds.TryGetValue(tokenInfo.TextValue, out var keywordKind))
                    tokenInfo.Kind = keywordKind;
                else if (tokenInfo.TextValue.StartsWith("int") && tokenInfo.TextValue.Length > 3 && tokenInfo.TextValue.AsSpan()[3..].All(SyntaxFacts.IsNumericLiteralDigit))
                {
                    tokenInfo.Kind = TokenKind.IntSized;
                    CheckSizedTypeKeyword(ref tokenInfo, 3);
                }
                else if (tokenInfo.TextValue.StartsWith("bool") && tokenInfo.TextValue.Length > 4 && tokenInfo.TextValue.AsSpan()[4..].All(SyntaxFacts.IsNumericLiteralDigit))
                {
                    tokenInfo.Kind = TokenKind.BoolSized;
                    CheckSizedTypeKeyword(ref tokenInfo, 4);
                }
                else if (tokenInfo.TextValue == "float16")
                {
                    tokenInfo.Kind = TokenKind.FloatSized;
                    tokenInfo.IntegerValue = 16;
                }
                else if (tokenInfo.TextValue == "float32")
                {
                    tokenInfo.Kind = TokenKind.FloatSized;
                    tokenInfo.IntegerValue = 32;
                }
                else if (tokenInfo.TextValue == "float64")
                {
                    tokenInfo.Kind = TokenKind.FloatSized;
                    tokenInfo.IntegerValue = 64;
                }
                else if (tokenInfo.TextValue == "float80")
                {
                    tokenInfo.Kind = TokenKind.FloatSized;
                    tokenInfo.IntegerValue = 80;
                }
                else if (tokenInfo.TextValue == "float128")
                {
                    tokenInfo.Kind = TokenKind.FloatSized;
                    tokenInfo.IntegerValue = 128;
                }

                void CheckSizedTypeKeyword(ref TokenInfo tokenInfo, int startLength)
                {
                    if (tokenInfo.TextValue.Length - startLength > 5)
                    {
                        Context.Diag.Error(new Location(tokenInfo.Position, 1, SourceFile.FileId), "sized primitive bit width must be in the range [1, 65536).");
                        return;
                    }

                    int bitWidth = int.Parse(tokenInfo.TextValue.AsSpan()[startLength..]);
                    if (bitWidth is < 1 or >= 65536)
                        Context.Diag.Error(new Location(tokenInfo.Position, 1, SourceFile.FileId), "sized primitive bit width must be in the range [1, 65536).");

                    tokenInfo.IntegerValue = bitWidth;
                }
            } break;

            case >= '0' and <= '9':
            {
                switch (ScanNumber())
                {
                    default: throw new UnreachableException();
                    case ScanNumberFlags.NotNumber: ReadIdentifier(ref tokenInfo); break;
                    case ScanNumberFlags.VanillaInteger: ReadInteger(ref tokenInfo); break;
                    case ScanNumberFlags.RadixInteger: ReadIntegerWithRadix(ref tokenInfo); break;
                    case ScanNumberFlags.VanillaFloat: ReadFloat(ref tokenInfo); break;
                    case ScanNumberFlags.RadixFloat: ReadFloatWithRadix(ref tokenInfo); break;
                }
            } break;

            case '@' when Peek(1) == '"':
            {
                Advance();
                ReadString(ref tokenInfo);
                tokenInfo.Kind = TokenKind.Identifier;
            } break;

            case '@' when Peek(1) is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_':
            {
                Advance();
                // we just don't transform it into a keyword here
                ReadIdentifier(ref tokenInfo);
            } break;

            case '@' when SyntaxFacts.IsIdentifierStartCharacter(Peek(1)):
            {
                Advance();
                // we just don't transform it into a keyword here
                ReadIdentifier(ref tokenInfo);
            } break;

            case '"': ReadString(ref tokenInfo); break;
            case '\'': ReadRune(ref tokenInfo); break;

            case '(': Advance(); tokenInfo.Kind = TokenKind.OpenParen; break;
            case ')': Advance(); tokenInfo.Kind = TokenKind.CloseParen; break;
            case '[': Advance(); tokenInfo.Kind = TokenKind.OpenBracket; break;
            case ']': Advance(); tokenInfo.Kind = TokenKind.CloseBracket; break;
            case '{': Advance(); tokenInfo.Kind = TokenKind.OpenBrace; break;
            case '}': Advance(); tokenInfo.Kind = TokenKind.CloseBrace; break;
            case ';': Advance(); tokenInfo.Kind = TokenKind.SemiColon; break;
            case ',': Advance(); tokenInfo.Kind = TokenKind.Comma; break;
            case '#' when Peek(1) == '[': Advance(); Advance(); tokenInfo.Kind = TokenKind.HashSquare; break;
            
            case '.':
            {
                Advance();
                tokenInfo.Kind
                        = TryAdvance('.')
                        ? TryAdvance('=') ? TokenKind.DotDotEqual : TokenKind.DotDot
                        : TokenKind.Dot;
            } break;

            case '~':
            {
                Advance();
                tokenInfo.Kind = TryAdvance('=') ? TokenKind.TildeEqual : TokenKind.Tilde;
            } break;

            case '!':
            {
                Advance();
                tokenInfo.Kind = TryAdvance('=') ? TokenKind.BangEqual : TokenKind.Bang;
            } break;

            case '%':
            {
                Advance();
                tokenInfo.Kind
                    = TryAdvance('=') ? TokenKind.PercentEqual
                    : TokenKind.Percent;
            } break;

            case '&':
            {
                Advance();
                tokenInfo.Kind = TryAdvance('=') ? TokenKind.AmpersandEqual : TokenKind.Ampersand;
            } break;

            case '*':
            {
                Advance();
                tokenInfo.Kind = TryAdvance('=') ? TokenKind.StarEqual : TokenKind.Star;
            } break;

            case '-':
            {
                Advance();
                tokenInfo.Kind
                    = TryAdvance('=') ? TokenKind.MinusEqual
                    : TryAdvance('-') ? TokenKind.MinusMinus
                    : TryAdvance('|')
                        ? TryAdvance('=') ? TokenKind.MinusPipeEqual : TokenKind.MinusPipe
                    : TryAdvance('%')
                        ? TryAdvance('=') ? TokenKind.MinusPercentEqual : TokenKind.MinusPercent
                    : TokenKind.Minus;
            } break;

            case '=':
            {
                Advance();
                tokenInfo.Kind = TryAdvance('=') ? TokenKind.EqualEqual
                               : TryAdvance('>') ? TokenKind.EqualGreater : TokenKind.Equal;
            } break;

            case '+':
            {
                Advance();
                tokenInfo.Kind
                    = TryAdvance('=') ? TokenKind.PlusEqual
                    : TryAdvance('+') ? TokenKind.PlusPlus
                    : TryAdvance('|')
                        ? TryAdvance('=') ? TokenKind.PlusPipeEqual : TokenKind.PlusPipe
                    : TryAdvance('%')
                        ? TryAdvance('=') ? TokenKind.PlusPercentEqual : TokenKind.PlusPercent
                    : TokenKind.Plus;
            } break;

            case '|':
            {
                Advance();
                tokenInfo.Kind = TryAdvance('=') ? TokenKind.PipeEqual : TokenKind.Pipe;
            } break;

            case ':':
            {
                Advance();
                tokenInfo.Kind
                    = TryAdvance(':') ? TokenKind.ColonColon
                    : TokenKind.Colon;
            } break;

            case '<':
            {
                Advance();
                tokenInfo.Kind
                    = TryAdvance('<')
                        ? TryAdvance('=')
                            ? TryAdvance('>') ? TokenKind.LessEqualGreater
                            : TokenKind.LessLessEqual
                        : TokenKind.LessLess
                    : TryAdvance('=') ? TokenKind.LessEqual
                    : TokenKind.Less;
            } break;

            case '>':
            {
                Advance();
                tokenInfo.Kind
                    = TryAdvance('=') ? TokenKind.GreaterEqual
                    : TryAdvance('>')
                        ? TryAdvance('>')
                            ? TryAdvance('=') ? TokenKind.GreaterGreaterGreaterEqual : TokenKind.GreaterGreaterGreater
                        : TryAdvance('=') ? TokenKind.GreaterGreaterEqual : TokenKind.GreaterGreater
                    : TokenKind.Greater;
            } break;

            case '/':
            {
                Advance();
                tokenInfo.Kind
                    = TryAdvance('=') ? TokenKind.SlashEqual
                    : TokenKind.Slash;
            } break;

            case '?':
            {
                Advance();
                tokenInfo.Kind
                    = TryAdvance('?')
                        ? TryAdvance('=') ? TokenKind.QuestionQuestionEqual : TokenKind.QuestionQuestion
                    : TokenKind.Question;
            } break;

            default:
            {
                Context.Assert(!IsAtEnd, CurrentLocation, "the lexer reached the end of file in an unexpected place");
                Context.Assert(CurrentCharacter != 0, CurrentLocation, "the lexer encountered an in-source NUL character in a location that is not supported");

                if (SyntaxFacts.IsIdentifierStartCharacter(CurrentCharacter))
                    ReadIdentifier(ref tokenInfo);
                else
                {
                    tokenInfo.Kind = TokenKind.Invalid;
                    Context.Diag.Error(CurrentLocation, $"Invalid character '{CurrentCharacter}' in Laye source.");
                    Advance();
                }
            } break;
        }

        tokenInfo.Length = Math.Max(0, _position - tokenInfo.Position);
        SkipTrivia();
    }

    private void SkipTrivia()
    {
        while (!IsAtEnd)
        {
            while (SyntaxFacts.IsWhiteSpaceCharacter(CurrentCharacter))
                Advance();

            switch (CurrentCharacter)
            {
                default: return;

                case '#' when Peek(1) == '!' && _position == 0:
                case '/' when Peek(1) == '/':
                {
                    Advance();
                    Advance();

                    while (!IsAtEnd && CurrentCharacter != '\n')
                        Advance();
                } break;

                case '/' when Peek(1) == '*':
                {
                    var location = CurrentLocation;

                    Advance();
                    Advance();

                    int nesting = 1;
                    while (!IsAtEnd && nesting > 0)
                    {
                        if (CurrentCharacter == '/' && Peek(1) == '*')
                        {
                            Advance();
                            Advance();
                            nesting++;
                        }
                        else if (CurrentCharacter == '*' && Peek(1) == '/')
                        {
                            Advance();
                            Advance();
                            nesting--;
                        }
                        else Advance();
                    }

                    if (nesting > 0)
                        Context.Diag.Error(location, "comment unclosed at end of file");
                } break;
            }
        }
    }

    private void ReadIdentifier(ref TokenInfo tokenInfo)
    {
        Context.Assert(SyntaxFacts.IsIdentifierStartCharacter(CurrentCharacter), CurrentLocation, "the lexer was instructed to read a Laye identifier, but the current character cannot start an identifier");

        tokenInfo.Kind = TokenKind.Identifier;
        while (!IsAtEnd)
        {
            if (CurrentCharacter is '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                Advance();
            else
            {
                if (SyntaxFacts.IsIdentifierPartCharacter(CurrentCharacter))
                    Advance();
                else break;
            }
        }

        tokenInfo.TextValue = _text.Substring(tokenInfo.Position, _position - tokenInfo.Position);
    }

    private void ReadInteger(ref TokenInfo tokenInfo)
    {
        Context.Assert(CurrentCharacter is >= '0' and <= '9', CurrentLocation, "the lexer was instructed to read a Laye integer literal, but the current character cannot start an integer literal");

        // when reading *just* an integer, we should be able to assert that
        // there are no identifier characters within or at its end.
        // the scanning process would trivially discard this case.
        
        tokenInfo.Kind = TokenKind.LiteralInteger;

        _stringBuilder.Clear();
        while (!IsAtEnd && CurrentCharacter is (>= '0' and <= '9') or '_')
        {
            if (CurrentCharacter != '_')
                _stringBuilder.Append(CurrentCharacter);
            Advance();
        }

        Context.Assert(CurrentCharacter is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_') && !SyntaxFacts.IsIdentifierPartCharacter(CurrentCharacter), CurrentLocation, "the lexer was instructed to read a Laye integer literal, but the current character cannot be a part of an integer literal");
        tokenInfo.IntegerValue = BigInteger.Parse(_stringBuilder.ToString());
    }

    private void ReadIntegerWithRadix(ref TokenInfo tokenInfo)
    {
        Context.Assert(CurrentCharacter is >= '0' and <= '9', CurrentLocation, "the lexer was instructed to read a Laye integer literal with a base prefix, but the current character cannot start an integer literal");

        var startLocation = CurrentLocation;
        tokenInfo.Kind = TokenKind.LiteralInteger;

        int radix;
        if (Peek(1) == '#')
        {
            radix = CurrentCharacter - '0';
            if (radix < 2)
            {
                Context.Diag.Error(startLocation, "integer base must be in the range [2, 36]");
                radix = 2;
            }

            Advance();
        }
        else if (Peek(1) is >= '0' and <= '9' && Peek(2) == '#')
        {
            radix = 10 * (CurrentCharacter - '0') + (Peek(1) - '0');
            if (radix > 36)
            {
                Context.Diag.Error(startLocation, "integer base must be in the range [2, 36]");
                radix = 36;
            }

            Advance();
            Advance();
        }
        else
        {
            Context.Diag.Error(startLocation, "integer base must be in the range [2, 36]");
            radix = 36;
            
            bool reportedIllegalUnderscore = false;
            while (CurrentCharacter != '#')
            {
                if (CurrentCharacter == '_' && !reportedIllegalUnderscore)
                {
                    Context.Diag.Error(CurrentLocation, "integer base must not contain '_' separators");
                    reportedIllegalUnderscore = true;
                }

                Advance();
            }
        }

        Context.Assert(!IsAtEnd && CurrentCharacter == '#', CurrentLocation, "the lexer was instructed to read a Laye integer literal with a base prefix, but the current character is not the base delimiter character '#'");
        Context.Assert(radix is >= 2 and <= 36, "the lexer failed to restrict the base of an integer literal to the range [2, 36]");
        Advance();

        _stringBuilder.Clear();
        
        bool wasLastCharacterUnderscore = false;
        bool parseResult = true;

        while (!IsAtEnd && CurrentCharacter is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' || SyntaxFacts.IsIdentifierPartCharacter(CurrentCharacter))
        {
            if (CurrentCharacter == '_')
            {
                while (CurrentCharacter == '_') Advance();
                wasLastCharacterUnderscore = true;
                continue;
            }
            else if (SyntaxFacts.IsIdentifierPartCharacter(CurrentCharacter) && CurrentCharacter is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'))
            {
                Context.Diag.Error(CurrentLocation, $"invalid digit '{CurrentCharacter}' in integer literal");
                while (SyntaxFacts.IsIdentifierPartCharacter(CurrentCharacter)) Advance();
                return;
            }

            if (!SyntaxFacts.IsNumericLiteralDigitInRadix(CurrentCharacter, radix))
            {
                Context.Diag.Error(CurrentLocation, $"invalid digit '{CurrentCharacter}' in base {radix} integer literal");
                parseResult = false;
                _stringBuilder.Clear();
            }
            
            wasLastCharacterUnderscore = false;
            if (parseResult) _stringBuilder.Append(CurrentCharacter);
            Advance();
        }

        if (wasLastCharacterUnderscore)
            Context.Diag.Error(startLocation, "integer literal cannot end with an '_' separator");

        if (parseResult)
        {
            Context.Assert(_stringBuilder.Length > 0, "the lexer expected to be able to parse an integer value, but no digits were provided to parse");
            tokenInfo.IntegerValue = ParseBigInteger(_stringBuilder.ToString(), radix);
        }
        else
        {
            Context.Assert(_stringBuilder.Length == 0, "the lexer expected to be unable to parse an integer value, so the digit storage string builder should have been cleared");
        }

        static BigInteger ParseBigInteger(string value, int radix)
        {
            if (radix == 16) return BigInteger.Parse("0" + value, System.Globalization.NumberStyles.HexNumber);
            var result = BigInteger.Zero;
            for (int i = 0; i < value.Length; i++)
                result = result * radix + SyntaxFacts.NumericLiteralDigitValueInRadix(value[i], radix);
            return result;
        }
    }

    private void ReadFloat(ref TokenInfo tokenInfo)
    {
        Context.Diag.ICE("float literal tokens are currently not supported :(");
        throw new UnreachableException();
    }

    private void ReadFloatWithRadix(ref TokenInfo tokenInfo)
    {
        Context.Diag.ICE("float literal tokens are currently not supported :(");
        throw new UnreachableException();
    }

    private int ReadEscapeSequenceAsSurrogatePair(out char highSurrogate, out char lowSurrogate)
    {
        highSurrogate = '\0';
        lowSurrogate = '\0';

        Context.Assert(CurrentCharacter == '\\', CurrentLocation, "the lexer was instructed to read an escape sequence, but the current character was not the escape start character '\\'");

        var startLocation = CurrentLocation;
        Advance();

        switch (CurrentCharacter)
        {
            case 'a': Advance(); lowSurrogate = '\a'; return 1;
            case 'b': Advance(); lowSurrogate = '\b'; return 1;
            case 'f': Advance(); lowSurrogate = '\f'; return 1;
            case 'n': Advance(); lowSurrogate = '\n'; return 1;
            case 'r': Advance(); lowSurrogate = '\r'; return 1;
            case 't': Advance(); lowSurrogate = '\t'; return 1;
            case 'v': Advance(); lowSurrogate = '\v'; return 1;
            case '\\': Advance(); lowSurrogate = '\\'; return 1;
            case '\'': Advance(); lowSurrogate = '\''; return 1;
            case '"': Advance(); lowSurrogate = '"'; return 1;

            case 'x' when SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(1), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(2), 16):
            {
                Advance();
                lowSurrogate = (char)int.Parse(_text.AsSpan().Slice(_position, 2), System.Globalization.NumberStyles.HexNumber);
                for (int i = 0; i < 2; i++) Advance();
                return 1;
            }

            case 'u' when SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(1), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(2), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(3), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(4), 16):
            {
                Advance();
                lowSurrogate = (char)int.Parse(_text.AsSpan().Slice(_position, 4), System.Globalization.NumberStyles.HexNumber);
                for (int i = 0; i < 4; i++) Advance();
                return 1;
            }

            case 'U' when SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(1), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(2), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(3), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(4), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(5), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(6), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(7), 16)
                       && SyntaxFacts.IsNumericLiteralDigitInRadix(Peek(8), 16):
            {
                Advance();
                int value = int.Parse(_text.AsSpan().Slice(_position, 8), System.Globalization.NumberStyles.HexNumber);
                unsafe
                {
                    char* pair = stackalloc char[2];
                    new Rune(value).EncodeToUtf16(new Span<char>(pair, 2));
                    highSurrogate = pair[0];
                    lowSurrogate = pair[1];
                }

                for (int i = 0; i < 8; i++) Advance();
                return 2;
            }

            default:
            {
                Context.Diag.Error(startLocation, "unrecognized escape sequence");
                lowSurrogate = CurrentCharacter;
                Advance();
                return 1;
            }
        }
    }

    private void ReadString(ref TokenInfo tokenInfo)
    {
        Context.Assert(CurrentCharacter == '"', CurrentLocation, "the lexer was instructed to read a Laye string literal, but the current character is not the string start character '\"'");

        var startLocation = CurrentLocation;
        tokenInfo.Kind = TokenKind.LiteralString;
        Advance();

        _stringBuilder.Clear();
        while (!IsAtEnd && CurrentCharacter != '"' && CurrentCharacter != '\n')
        {
            if (CurrentCharacter == '\\')
            {
                int nchars = ReadEscapeSequenceAsSurrogatePair(out char higher, out char lower);
                Context.Assert(nchars == 1 || nchars == 2, "the lexer requested an escape sequence to be read, but the number of characters returned was not the expected 1 or 2");
                _stringBuilder.Append(lower);
                if (nchars == 2) _stringBuilder.Append(higher);
            }
            else
            {
                _stringBuilder.Append(CurrentCharacter);
                Advance();
            }
        }

        tokenInfo.TextValue = _stringBuilder.ToString();
        if (TryAdvance('"')) return;

        if (IsAtEnd)
        {
            Context.Diag.Error(startLocation, "end of file reached in string literal");
            return;
        }

        if (CurrentCharacter != '"')
        {
            Context.Diag.Error(startLocation, "newline in string literal");
            return;
        }
    }

    private void ReadRune(ref TokenInfo tokenInfo)
    {
        Context.Assert(CurrentCharacter == '\'', CurrentLocation, "the lexer was instructed to read a Laye rune literal, but the current character is not the rune start character '\''");
        
        var startLocation = CurrentLocation;
        tokenInfo.Kind = TokenKind.LiteralRune;
        Advance();
        
        if (CurrentCharacter == '\\')
        {
            int nchars = ReadEscapeSequenceAsSurrogatePair(out char higher, out char lower);
            tokenInfo.IntegerValue = new BigInteger(nchars == 1 ? lower : char.ConvertToUtf32(higher, lower));
        }
        else
        {
            tokenInfo.IntegerValue = new BigInteger(CurrentCharacter);
            Advance();
        }
        
        if (TryAdvance('\'')) return;

        if (IsAtEnd)
        {
            Context.Diag.Error(startLocation, "end of file reached in rune literal");
            return;
        }

        if (CurrentCharacter != '\'')
        {
            while (!IsAtEnd && CurrentCharacter != '\n') Advance();
            Context.Diag.Error(startLocation, "too many characters in rune literal");
            return;
        }
    }

    private ScanNumberFlags ScanNumber()
    {
        Context.Assert(SyntaxFacts.IsNumericLiteralDigit(CurrentCharacter), CurrentLocation, "the lexer was instructed to scan ahead to determine the kind of Laye numeric literal to read, if any, but the current character cannot start a numeric literal.");

        using var resetPoint = new LexerResetPoint(this);
        while (!IsAtEnd)
        {
            switch (CurrentCharacter)
            {
                case >= '0' and <= '9': Advance(); break;
                case '_' when IsSequenceOfUnderscoresFollowedByDigit():
                {
                    while (CurrentCharacter == '_') Advance();
                    Advance(); // the digit
                } break;

                // an underscore with no following digit, or any letters, means this is
                // definitively not going to be a number.
                case '_':
                case (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'):
                    return ScanNumberFlags.NotNumber;

                case '.': return ScanNumberFlags.VanillaFloat;
                case '#': return ScanNumberWithRadix();
                default: return SyntaxFacts.IsIdentifierPartCharacter(CurrentCharacter) ? ScanNumberFlags.NotNumber : ScanNumberFlags.VanillaInteger;
            }
        }

        return ScanNumberFlags.VanillaInteger;

        bool IsSequenceOfUnderscoresFollowedByDigit()
        {
            for (int i = 0; i < 10; i++)
            {
                if (Peek(i) != '_') return false;
                if (Peek(i + 1) is >= '0' and <= '9') return true;
            }

            return false;
        }

        ScanNumberFlags ScanNumberWithRadix()
        {
            Context.Assert(CurrentCharacter == '#', "the lexer was instructed to scan a Laye numeric literal with a base, but the current character is not the base delimiter character '#'");
            Advance();

            while (!IsAtEnd)
            {
                // at this point we're beyond the point of no return for identifier fallbacks
                if (CurrentCharacter is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') || SyntaxFacts.IsIdentifierPartCharacter(CurrentCharacter))
                    Advance();
                else if (CurrentCharacter == '.')
                    return ScanNumberFlags.RadixFloat;
                else break;
            }

            return ScanNumberFlags.RadixInteger;
        }
    }
}
