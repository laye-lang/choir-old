using Choir.Source;

namespace Choir.FrontEnd.C.Preprocess;

public sealed class CPreprocessor
{
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

        public bool IsAtEnd => Tokens[ReadPosition].Kind == CPPTokenKind.EndOfFile;
    }

    public static List<CSyntaxToken> PreprocessTokens(CCContext context, SourceText source, IReadOnlyList<CPPToken> ppTokens)
    {
        var pp = new CPreprocessor(context, source, ppTokens);
        var tokens = new List<CSyntaxToken>();

        return tokens;
    }

    private readonly CCContext _context;
    private readonly Stack<PPState> _stack = [];

    private PPState CurrentState => _stack.Peek();

    private CPreprocessor(CCContext context, SourceText source, IReadOnlyList<CPPToken> tokens)
    {
        _context = context;
        _stack.Push(new PPState(source, tokens));
    }

    private CSyntaxToken ReadSyntaxToken()
    {
        throw new NotImplementedException();
    }
}
