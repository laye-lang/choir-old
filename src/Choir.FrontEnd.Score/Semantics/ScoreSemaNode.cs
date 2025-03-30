using Choir.Source;

namespace Choir.FrontEnd.Score.Semantics;

public abstract class ScoreSemaNode(SourceRange range)
    : IEquatable<ScoreSemaNode>
    , ITreeDebugNode
{
    private static long _counter = 0;

    public long Id { get; } = Interlocked.Increment(ref _counter);

    public SourceRange Range { get; } = range;
    public SourceLocation Location { get; } = range.Begin;

    public abstract string DebugNodeName { get; }
    public abstract IEnumerable<ScoreSemaNode> Children { get; }

    public override int GetHashCode() => HashCode.Combine(Id);

    public override bool Equals(object? obj) => obj is ScoreSemaNode other && Equals(other);
    public virtual bool Equals(ScoreSemaNode? other) => other is not null && Id == other.Id;
}
