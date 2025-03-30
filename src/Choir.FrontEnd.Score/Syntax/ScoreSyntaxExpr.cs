using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public abstract class ScoreSyntaxExpr(SourceRange range)
    : ScoreSyntaxNode(range)
{
}

/// <summary>
/// For expressions which don't come with a semicolon in a statement context require one.
/// </summary>
public sealed class ScoreSyntaxExprStmt(ScoreSyntaxExpr expr, ScoreSyntaxToken semiColonToken)
    : ScoreSyntaxExpr(expr.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxExprStmt);

    public ScoreSyntaxExpr Expr { get; } = expr;
    public ScoreSyntaxToken SemiColonToken { get; } = semiColonToken;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [expr, semiColonToken];
}

#region Statements

public sealed class ScoreSyntaxExprReturn(ScoreSyntaxToken returnKeywordToken, ScoreSyntaxExpr? returnValue, ScoreSyntaxToken semiColonToken)
    : ScoreSyntaxExpr(returnKeywordToken.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxExprReturn);

    public ScoreSyntaxToken ReturnKeywordToken { get; } = returnKeywordToken;
    public ScoreSyntaxExpr? ReturnValue { get; } = returnValue;
    public ScoreSyntaxToken SemiColonToken { get; } = semiColonToken;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = returnValue is null ? [returnKeywordToken, semiColonToken] : [returnKeywordToken, returnValue, semiColonToken];
}

#endregion

#region Expressions

public sealed class ScoreSyntaxExprLiteral(ScoreSyntaxToken literalToken)
    : ScoreSyntaxExpr(literalToken.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxExprLiteral);

    public ScoreSyntaxToken LiteralToken { get; } = literalToken;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [literalToken];
}

public sealed class ScoreSyntaxExprCompound(ScoreSyntaxToken openCurlyToken, IReadOnlyList<ScoreSyntaxNode> childNodes, ScoreSyntaxToken closeCurlyToken)
    : ScoreSyntaxExpr(openCurlyToken.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxExprCompound);

    public ScoreSyntaxToken OpenCurlyToken { get; } = openCurlyToken;
    public IReadOnlyList<ScoreSyntaxNode> ChildNodes { get; } = childNodes;
    public ScoreSyntaxToken CloseCurlyToken { get; } = closeCurlyToken;
    public override IEnumerable<ScoreSyntaxNode> Children { get; } = [openCurlyToken, .. childNodes, closeCurlyToken];
}

#endregion
