using Choir.Source;

namespace Choir.FrontEnd.C.Preprocess;

public sealed class CPreprocessor
{
    private sealed class PPMacroExpansion(CPPMacroDef def, IReadOnlyList<IReadOnlyList<CSyntaxToken>> args)
    {
        public readonly CPPMacroDef Def = def;
        public readonly IReadOnlyList<IReadOnlyList<CSyntaxToken>> Args = args;

        public int BodyPosition;

        public int ArgIndex = -1;
        public int ArgPosition;
    }

    private sealed class PPState(SourceText source, IReadOnlyList<CPPToken> tokens)
    {
        public readonly SourceText Source = source;
        public readonly IReadOnlyList<CPPToken> Tokens = tokens;

        private int _readPosition;
        public int ReadPosition
        {
            get => _readPosition;
            set => _readPosition = Math.Clamp(value, 0, Tokens.Count - 1);
        }

        public bool IsAtEnd => Tokens[ReadPosition].Kind is CPPTokenKind.EndOfFile;
    }

    public static List<CSyntaxToken> PreprocessTokens(CCContext context, CPPMacroStore macroStore, SourceText source, IReadOnlyList<CPPToken> ppTokens)
    {
        var pp = new CPreprocessor(context, macroStore, source, ppTokens);
        var tokens = new List<CSyntaxToken>();
        
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
    private readonly IReadOnlyList<CPPToken> _tokens;
    private readonly Stack<PPMacroExpansion> _expansions = [];

    private int _readPosition;

    private bool IsAtEnd => _readPosition >= _tokens.Count;
    private CPPToken CurrentPPToken => PeekPPToken(0);
    private SourceLocation CurrentLocation => CurrentPPToken.Location;

    private CPreprocessor(CCContext context, CPPMacroStore macroStore, SourceText source, IReadOnlyList<CPPToken> tokens)
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

    private CPPToken PeekPPToken(int ahead = 0)
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

    private CSyntaxToken ReadSyntaxToken()
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

                return nextToken;
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

        var currentToken = CurrentPPToken;
        switch (currentToken)
        {
            default:
            {
                Advance();
                return TransformPPToken(currentToken);
            }
        }
    }

    private CSyntaxToken TransformPPToken(CPPToken ppToken)
    {
        throw new NotImplementedException();
    }
}
