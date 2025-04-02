using System.Diagnostics;

using Choir.Diagnostics;
using Choir.FrontEnd.C.Diagnostics;
using Choir.Source;

namespace Choir.FrontEnd.C.Preprocess;

public sealed class CPreprocessor
{
    private sealed class PPMacroExpansion(CPPMacroDef def, IReadOnlyList<IReadOnlyList<CToken>> args)
    {
        public readonly CPPMacroDef Def = def;
        public readonly IReadOnlyList<IReadOnlyList<CToken>> Args = args;

        public int BodyPosition;

        public int ArgIndex = -1;
        public int ArgPosition;
    }

    private sealed class PPState(SourceText source, IReadOnlyList<CToken> tokens)
    {
        public readonly SourceText Source = source;
        public readonly IReadOnlyList<CToken> Tokens = tokens;

        private int _readPosition;
        public int ReadPosition
        {
            get => _readPosition;
            set => _readPosition = Math.Clamp(value, 0, Tokens.Count - 1);
        }

        public bool IsAtEnd => Tokens[ReadPosition].Kind is CTokenKind.EndOfFile;
    }

    public static List<CToken> PreprocessTokens(CCContext context, CPPMacroStore macroStore, SourceText source, IReadOnlyList<CToken> ppTokens)
    {
        Debug.Assert(ppTokens.Count == 0 || ppTokens[^1].Kind == CTokenKind.EndOfFile);

        var pp = new CPreprocessor(context, macroStore, source, ppTokens);
        var tokens = new List<CToken>();
        
        while (true)
        {
            var token = pp.ReadSyntaxToken();
            tokens.Add(token);

            if (token.Kind is CTokenKind.EndOfFile)
                break;
        }

        return tokens;
    }

    private readonly CCContext _context;
    private readonly CPPMacroStore _macros;
    private readonly SourceText _source;
    private readonly IReadOnlyList<CToken> _tokens;
    private readonly Stack<PPMacroExpansion> _expansions = [];

    private int _readPosition;

    private bool IsAtEnd => _readPosition >= _tokens.Count;
    private CToken CurrentToken => PeekPPToken(0);
    private SourceLocation CurrentLocation => CurrentToken.Location;

    private CPreprocessor(CCContext context, CPPMacroStore macroStore, SourceText source, IReadOnlyList<CToken> tokens)
    {
        _context = context;
        _macros = macroStore;
        _source = source;
        _tokens = tokens;
    }

    private void Advance()
    {
        _readPosition = Math.Min(_readPosition + 1, _tokens.Count - 1);
    }

    private bool TryAdvance(CTokenKind kind)
    {
        if (CurrentToken.Kind != kind)
            return false;

        Advance();
        return true;
    }

    private CToken PeekPPToken(int ahead = 0)
    {
        _context.Assert(ahead >= 0, $"Parameter {nameof(ahead)} to function {nameof(CPreprocessor)}::{nameof(PeekPPToken)} must be non-negative; the preprocessor should never rely on token look-back.");

        int peekIndex = _readPosition + ahead;
        if (peekIndex >= _tokens.Count)
        {
            _context.Assert(_tokens.Count > 0, "Tokens should contain at least an EOF token at all times.");
            return _tokens[^1];
        }

        return _tokens[peekIndex];
    }

    private CToken ReadSyntaxToken()
    {
        if (_expansions.TryPeek(out var currentExpansion))
        {
            var body = currentExpansion.Def.Body;
            if (currentExpansion.ArgIndex >= 0)
            {
                var argTokens = currentExpansion.Args[currentExpansion.ArgIndex];

                var nextToken = argTokens[currentExpansion.ArgPosition];
                currentExpansion.ArgPosition++;

                if (currentExpansion.ArgPosition >= argTokens.Count)
                {
                    currentExpansion.ArgIndex = -1;
                    currentExpansion.ArgPosition = 0;

                    if (currentExpansion.BodyPosition >= body.Count)
                        _expansions.Pop();
                }

                return TransformPPToken(nextToken);
            }
            else
            {
                if (currentExpansion.BodyPosition >= body.Count)
                {
                    _expansions.Pop();
                    goto regular_lex_token;
                }

                var nextToken = body[currentExpansion.BodyPosition];
                currentExpansion.BodyPosition++;

                if (nextToken.IsMacroParam)
                {
                    currentExpansion.ArgIndex = nextToken.MacroParamIndex;
                    currentExpansion.ArgPosition = 0;
                    return ReadSyntaxToken();
                }

                if (currentExpansion.BodyPosition >= body.Count)
                    _expansions.Pop();

                return nextToken;
            }
        }

    regular_lex_token:;

        while (CurrentToken is { Kind: CTokenKind.Pound, IsAtStartOfLine: true })
            HandlePreprocessorDirective();

        var currentToken = CurrentToken;
        Advance();

        if (currentToken.Kind is CTokenKind.Identifier)
        {
            var name = currentToken.StringValue;
            if (_macros.TryGetMacroDef(name, out var macroDef))
            {
                List<List<CToken>> macroArgs = [];
                if (macroDef.HasParams)
                {
                    if (CurrentToken.Kind is CTokenKind.OpenParen && CurrentLocation == currentToken.Range.End)
                    {
                        Advance(); // past the opening paren

                        var argTokens = new List<CToken>();
                        macroArgs.Add(argTokens);

                        while (!IsAtEnd && CurrentToken.Kind is not CTokenKind.CloseParen)
                        {
                            int parenNesting = 0;
                            while (!IsAtEnd)
                            {
                                if (parenNesting == 0 && CurrentToken.Kind is CTokenKind.Comma)
                                    break;

                                var argToken = CurrentToken;
                                if (argToken.Kind is CTokenKind.OpenParen)
                                    parenNesting++;
                                else if (argToken.Kind is CTokenKind.CloseParen)
                                {
                                    Debug.Assert(parenNesting > 0);
                                    parenNesting--;
                                }

                                argTokens.Add(argToken);
                            }

                            if (!TryAdvance(CTokenKind.Comma))
                                break;

                            // set up the next argument token list, then continue looping
                            argTokens = [];
                            macroArgs.Add(argTokens);
                        }

                        if (!TryAdvance(CTokenKind.CloseParen))
                            _context.ErrorUnterminatedFunctionLikeMacro(_source, currentToken.Location, macroDef.NameToken);
                    }
                    else
                    {
                        // the macro we found has parameters, but we don't provide arguments.
                        // in this case, the macro is not expanded.
                        goto just_return_the_token;
                    }
                }

                var expansion = new PPMacroExpansion(macroDef, macroArgs);
                _expansions.Push(expansion);

                return ReadSyntaxToken();
            }
        }

    just_return_the_token:;
        return TransformPPToken(currentToken);
    }

    private CToken TransformPPToken(CToken ppToken)
    {
        switch (ppToken.Kind)
        {
            default: return ppToken;

            case CTokenKind.Identifier:
            {
                //_context.Todo(_source, ppToken.Location, "Implement identifier -> keyword transformation");
                return ppToken;
            }
        }
    }

    private void SkipToEndOfDirectiveTokens(string directiveKind, bool reportExtraTokens = true)
    {
        if (reportExtraTokens && !IsAtEnd && CurrentToken is not { Kind: CTokenKind.DirectiveEnd })
            _context.WarningExtraTokensAtEndOfDirective(_source, CurrentLocation, directiveKind);

        while (!IsAtEnd && CurrentToken is not { Kind: CTokenKind.DirectiveEnd })
            Advance();
    }

    private void HandlePreprocessorDirective()
    {
        Debug.Assert(CurrentToken is { Kind: CTokenKind.Pound, IsAtStartOfLine: true });
        Advance();

        var directiveToken = CurrentToken;
        switch (directiveToken.Kind)
        {
            default:
            {
                _context.Todo(_source, directiveToken.Location, "Handle an unrecognized directive token");
                throw new UnreachableException();
            }

            case CTokenKind.Identifier when directiveToken.StringValue == "define":
            {
                Advance();

                var macroNameToken = CurrentToken;
                if (macroNameToken.Kind is not CTokenKind.Identifier)
                {
                    _context.ErrorMacroNameMissing(_source, macroNameToken.Location);
                    SkipToEndOfDirectiveTokens("#define");
                    break;
                }
                
                Advance();

                List<CToken>? paramTokens = null;
                if (CurrentToken.Kind == CTokenKind.OpenParen && CurrentLocation == directiveToken.Range.End)
                {
                    _context.Todo("Handle parsing function-like macros");
                }

                var bodyTokens = new List<CToken>();
                while (!IsAtEnd && CurrentToken.Kind is not CTokenKind.DirectiveEnd)
                {
                    bodyTokens.Add(CurrentToken);
                    Advance();
                }

                var macroDef = new CPPMacroDef(macroNameToken, paramTokens, bodyTokens);
                _macros.Define(macroDef);

                // shouldn't actually do much of anything, but here for completeness
                SkipToEndOfDirectiveTokens("#define");
            } break;
        }

        if (!IsAtEnd && !TryAdvance(CTokenKind.DirectiveEnd))
            _context.Unreachable("Should either be at the end of the file, or at the <directive-end> special token. Neither was found.");
    }
}
