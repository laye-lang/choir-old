using Choir.FrontEnd.Score.Syntax;
using Choir.Source;

namespace Choir.FrontEnd.Score.Semantics;

public sealed class ScoreSemaUnit(SourceText source, IReadOnlyList<ScoreSyntaxToken> tokens, IReadOnlyList<ScoreSemaNode> topLevelNodes)
    : ScoreSemaNode(new SourceRange())
{
    public override string DebugNodeName { get; } = nameof(ScoreSemaUnit);

    public SourceText Source { get; } = source;
    public IReadOnlyList<ScoreSyntaxToken> Tokens { get; } = tokens;
    public IReadOnlyList<ScoreSemaNode> TopLevelNodes { get; } = topLevelNodes;

    public override IEnumerable<ScoreSemaNode> Children { get; } = topLevelNodes;
}
