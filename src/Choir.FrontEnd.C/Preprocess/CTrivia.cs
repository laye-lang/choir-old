using Choir.Source;

namespace Choir.FrontEnd.C.Preprocess;

public abstract class CTrivia(SourceRange range)
    : ITreeDebugNode
{
    public SourceRange Range { get; } = range;
    public abstract string DebugNodeName { get; }
    public IEnumerable<ITreeDebugNode> Children { get; } = [];
}

public sealed class CTriviaList(IReadOnlyList<CTrivia> trivia, bool isLeading)
    : ITreeDebugNode
{
    public IReadOnlyList<CTrivia> Trivia { get; } = trivia;
    public bool IsLeading { get; set; } = isLeading;

    public string DebugNodeName { get; } = nameof(CTriviaList);
    public IEnumerable<ITreeDebugNode> Children { get; } = trivia;
}

public sealed class CTriviaWhiteSpace(SourceRange range)
    : CTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(CTriviaWhiteSpace);
}

public sealed class CTriviaNewLine(SourceRange range)
    : CTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(CTriviaNewLine);
}

public sealed class CTriviaShebangComment(SourceRange range)
    : CTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(CTriviaShebangComment);
}

public sealed class CTriviaLineComment(SourceRange range)
    : CTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(CTriviaLineComment);
}

public sealed class CTriviaDelimitedComment(SourceRange range)
    : CTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(CTriviaDelimitedComment);
}
