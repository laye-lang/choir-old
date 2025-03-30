using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public sealed class ScoreSyntaxUnit(SourceText source, IReadOnlyList<ScoreSyntaxToken> tokens, IReadOnlyList<ScoreSyntaxNode> topLevelNodes)
    : ScoreSyntaxNode(new())
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxUnit);

    public SourceText Source { get; } = source;
    public IReadOnlyList<ScoreSyntaxToken> Tokens { get; } = tokens;
    public IReadOnlyList<ScoreSyntaxNode> TopLevelNodes { get; } = topLevelNodes;

    public override IEnumerable<ScoreSyntaxNode> Children { get; } = topLevelNodes;
}

public sealed class ScoreSyntaxEndOfUnit(ScoreSyntaxToken endOfFileToken)
    : ScoreSyntaxNode(endOfFileToken.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxEndOfUnit);

    public ScoreSyntaxToken EndOfFileToken { get; set; } = endOfFileToken;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [endOfFileToken];
}
