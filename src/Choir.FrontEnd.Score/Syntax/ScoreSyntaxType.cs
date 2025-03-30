using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public abstract class ScoreSyntaxType(SourceRange range)
    : ScoreSyntaxNode(range)
{
}

public sealed class ScoreSyntaxTypeQual(ScoreSyntaxType? underlyingSyntaxType, SourceRange range = default)
    : ScoreSyntaxNode(underlyingSyntaxType?.Range ?? range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxTypeQual);

    public ScoreSyntaxType? UnderlyingSyntaxType { get; } = underlyingSyntaxType;
    public ScoreSyntaxToken? ReadAccessKeywordToken { get; init; }
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = underlyingSyntaxType is null ? [] : [underlyingSyntaxType];
}

public sealed class ScoreSyntaxTypeBuiltin(ScoreSyntaxToken typeKeywordToken)
    : ScoreSyntaxType(typeKeywordToken.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxTypeBuiltin);

    public ScoreSyntaxToken TypeKeywordToken { get; } = typeKeywordToken;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [typeKeywordToken];
}

public sealed class ScoreSyntaxTypeBuffer(ScoreSyntaxToken openSquareToken, ScoreSyntaxToken starToken, ScoreSyntaxToken closeSquareToken, ScoreSyntaxTypeQual elementType)
    : ScoreSyntaxType(new(openSquareToken.Range.Begin, elementType.Range.End))
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxTypeBuffer);

    public ScoreSyntaxToken OpenSquareToken { get; } = openSquareToken;
    public ScoreSyntaxToken StarToken { get; } = starToken;
    public ScoreSyntaxToken CloseSquareToken { get; } = closeSquareToken;
    public ScoreSyntaxTypeQual ElementType { get; } = elementType;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [openSquareToken, starToken, closeSquareToken, elementType];
}
