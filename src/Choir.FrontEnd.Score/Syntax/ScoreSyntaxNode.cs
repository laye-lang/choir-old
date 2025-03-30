using Choir.FrontEnd.Score.Semantics;
using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public abstract class ScoreSyntaxNode(SourceRange range)
    : IEquatable<ScoreSyntaxNode>
    , ITreeDebugNode
{
    private static long _counter = 0;

    public long Id { get; } = Interlocked.Increment(ref _counter);

    public SourceRange Range { get; } = range;
    public SourceLocation Location { get; } = range.Begin;

    public abstract string DebugNodeName { get; }
    public abstract IEnumerable<ScoreSyntaxNode> Children { get; }

    public ScoreSemaNode? SemaNode { get; set; }

    public ReadOnlyMemory<char> GetSourceSlice(SourceText source) => source.Slice(Range);
    public string GetSourceSubstring(SourceText source) => source.Substring(Range);

    public override int GetHashCode() => HashCode.Combine(Id);

    public override bool Equals(object? obj) => obj is ScoreSyntaxNode other && Equals(other);
    public virtual bool Equals(ScoreSyntaxNode? other) => other is not null && Id == other.Id;
}
