using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public enum OverloadableOperator
{
    Bang,
    BangEqual,
    Percent,
    Ampersand,
    Star,
    Plus,
    Minus,
    Slash,
    Less,
    LessEqual,
    LessGreaterEqual,
    LessLess,
    EqualEqual,
    Greater,
    GreaterEqual,
    GreaterGreater,
    GreaterGreaterGreater,
    Pipe,
    Tilde,

    Call,
    True,
    False,
}

public abstract class ScoreSyntaxName(SourceRange range)
    : ScoreSyntaxNode(range)
{
}

public sealed class ScoreSyntaxNameIdentifier(ScoreSyntaxToken identifierToken)
    : ScoreSyntaxName(identifierToken.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxNameIdentifier);

    public ScoreSyntaxToken IdentifierToken { get; } = identifierToken;
    public ReadOnlyMemory<char> Spelling => IdentifierToken.StringValue;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [identifierToken];
}

public sealed class ScoreSyntaxNameOperator(OverloadableOperator @operator, IReadOnlyList<ScoreSyntaxToken> operatorTokens)
    : ScoreSyntaxName(new(operatorTokens[0].Range.Begin, operatorTokens[^1].Range.End))
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxNameOperator);

    public OverloadableOperator Operator { get; } = @operator;
    public IReadOnlyList<ScoreSyntaxToken> OperatorTokens { get; } = operatorTokens;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = operatorTokens;
}

public sealed class ScoreSyntaxNameOperatorCast(ScoreSyntaxToken castKeywordToken, ScoreSyntaxToken openParenToken, ScoreSyntaxTypeQual castType, ScoreSyntaxToken closeParenToken)
    : ScoreSyntaxName(new(castKeywordToken.Range.Begin, closeParenToken.Range.End))
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxNameOperatorCast);

    public ScoreSyntaxToken CastKeywordToken { get; } = castKeywordToken;
    public ScoreSyntaxToken OpenParenToken { get; } = openParenToken;
    public ScoreSyntaxTypeQual CastType { get; } = castType;
    public ScoreSyntaxToken CloseParenToken { get; } = closeParenToken;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [castKeywordToken, openParenToken, castType, closeParenToken];
}
