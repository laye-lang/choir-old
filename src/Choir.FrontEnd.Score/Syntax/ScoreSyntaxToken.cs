using System.Numerics;

using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public sealed class ScoreSyntaxToken(ScoreTokenKind kind, SourceRange range, ScoreTriviaList leadingTrivia, ScoreTriviaList trailingTrivia)
    : ScoreSyntaxNode(range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxToken);

    public ScoreTokenKind Kind { get; } = kind;

    public ReadOnlyMemory<char> StringValue { get; init; }
    public BigInteger IntegerValue { get; init; }
    public double FloatValue { get; init; }

    public ScoreTriviaList LeadingTrivia { get; } = leadingTrivia;
    public ScoreTriviaList TrailingTrivia { get; } = trailingTrivia;

    public override IEnumerable<ITreeDebugNode> Children
    {
        get
        {
            if (LeadingTrivia.Trivia.Count != 0)
                yield return LeadingTrivia;
            if (TrailingTrivia.Trivia.Count != 0)
                yield return TrailingTrivia;
        }
    }
}
