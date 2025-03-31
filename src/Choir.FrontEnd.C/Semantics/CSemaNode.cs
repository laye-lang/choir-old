using Choir.Source;

namespace Choir.FrontEnd.C.Semantics;

public abstract class CSemaNode(SourceText source, SourceRange range)
    : IEquatable<CSemaNode>
    , ITreeDebugNode
{
    private static long _counter = 0;

    public long Id { get; } = Interlocked.Increment(ref _counter);

    public SourceText Source { get; } = source;
    public SourceRange Range { get; } = range;
    public SourceLocation Location { get; } = range.Begin;

    public abstract string DebugNodeName { get; }
    public abstract IEnumerable<ITreeDebugNode> Children { get; }

    public ReadOnlyMemory<char> GetSourceSlice(SourceText source) => source.Slice(Range);
    public string GetSourceSubstring(SourceText source) => source.Substring(Range);

    public override int GetHashCode() => HashCode.Combine(Id);

    public override bool Equals(object? obj) => obj is CSemaNode other && Equals(other);
    public virtual bool Equals(CSemaNode? other) => other is not null && Id == other.Id;
}
