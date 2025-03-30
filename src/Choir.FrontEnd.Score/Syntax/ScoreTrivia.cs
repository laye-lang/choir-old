using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public abstract class ScoreTrivia(SourceRange range)
    : ScoreSyntaxNode(range)
{
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [];
}

public sealed class ScoreTriviaList(IReadOnlyList<ScoreTrivia> trivia, bool isLeading)
    : ScoreSyntaxNode(trivia.Count == 0 ? new() : new(trivia[0].Range.Begin, trivia[^1].Range.End))
{
    public override string DebugNodeName { get; } = nameof(ScoreTriviaList);

    public IReadOnlyList<ScoreTrivia> Trivia { get; } = trivia;
    public bool IsLeading { get; set; } = isLeading;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = trivia;
}

public sealed class ScoreTriviaWhiteSpace(SourceRange range)
    : ScoreTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(ScoreTriviaWhiteSpace);
}

public sealed class ScoreTriviaNewLine(SourceRange range)
    : ScoreTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(ScoreTriviaNewLine);
}

public sealed class ScoreTriviaShebangComment(SourceRange range)
    : ScoreTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(ScoreTriviaShebangComment);
}

public sealed class ScoreTriviaLineComment(SourceRange range)
    : ScoreTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(ScoreTriviaLineComment);
}

public sealed class ScoreTriviaDelimitedComment(SourceRange range)
    : ScoreTrivia(range)
{
    public override string DebugNodeName { get; } = nameof(ScoreTriviaDelimitedComment);
}
