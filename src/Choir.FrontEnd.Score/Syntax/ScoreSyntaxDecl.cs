using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public abstract class ScoreSyntaxDecl(SourceRange range)
    : ScoreSyntaxNode(range)
{
}

public abstract class ScoreSyntaxDeclNamed(ScoreSyntaxName name)
    : ScoreSyntaxDecl(name.Range)
{
    public ScoreSyntaxName Name { get; } = name;
}

public sealed class ScoreSyntaxDeclFunc(ScoreSyntaxToken funcKeywordToken, ScoreSyntaxName funcName,
    ScoreSyntaxToken openParenToken, ScoreSyntaxDeclFuncParams declParams, ScoreSyntaxToken closeParenToken,
    ScoreSyntaxToken? arrowToken, ScoreSyntaxTypeQual? returnType, ScoreSyntaxFuncBody funcBody)
    : ScoreSyntaxDeclNamed(funcName)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxDeclFunc);

    public ScoreSyntaxToken FuncKeywordToken { get; } = funcKeywordToken;
    public ScoreSyntaxToken OpenParenToken { get; } = openParenToken;
    public ScoreSyntaxDeclFuncParams DeclParams { get; } = declParams;
    public ScoreSyntaxToken CloseParenToken { get; } = closeParenToken;
    public ScoreSyntaxToken? ArrowToken { get; } = arrowToken;
    public ScoreSyntaxTypeQual? ReturnType { get; } = returnType;
    public ScoreSyntaxFuncBody FuncBody { get; } = funcBody;

    public ScoreSyntaxDeclFunc(ScoreSyntaxToken funcKeywordToken, ScoreSyntaxName funcName, ScoreSyntaxToken openParenToken,
        ScoreSyntaxDeclFuncParams declParams, ScoreSyntaxToken closeParenToken, ScoreSyntaxFuncBody funcBody)
        : this(funcKeywordToken, funcName, openParenToken, declParams, closeParenToken, null, null, funcBody)
    { 
    }

    public override IEnumerable<ITreeDebugNode> Children { get; } = [funcKeywordToken, funcName, openParenToken, declParams, closeParenToken,
        .. new ScoreSyntaxNode?[] { arrowToken, returnType }.Where(n => n is not null).Cast<ScoreSyntaxNode>(), funcBody];
}

public class ScoreSyntaxDeclFuncParams(List<ScoreSyntaxDeclFuncParam> declParams, List<ScoreSyntaxToken> commaTokens)
    : ScoreSyntaxNode(declParams.Count == 0 ? new() : new(declParams[0].Range.Begin, declParams[^1].Range.End))
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxDeclFuncParams);

    public List<ScoreSyntaxDeclFuncParam> DeclParams { get; } = declParams;
    public List<ScoreSyntaxToken> CommaTokens { get; } = commaTokens;

    public override IEnumerable<ITreeDebugNode> Children { get; } = declParams
        .Select(p => (p.Range, Node: (ScoreSyntaxNode)p))
        .Concat(commaTokens.Select(p => (p.Range, Node: (ScoreSyntaxNode)p)))
        .OrderBy(pair => pair.Range)
        .Select(pair => pair.Node);
}

public class ScoreSyntaxDeclFuncParam(ScoreSyntaxName paramName, ScoreSyntaxToken colonToken, ScoreSyntaxTypeQual paramType)
    : ScoreSyntaxDeclNamed(paramName)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxDeclFuncParam);

    public ScoreSyntaxToken ColonToken { get; } = colonToken;
    public ScoreSyntaxTypeQual ParamType { get; } = paramType;
    public override IEnumerable<ITreeDebugNode> Children { get; } = [paramName, colonToken, paramType];
}

public abstract class ScoreSyntaxFuncBody(SourceRange range)
    : ScoreSyntaxNode(range)
{
}

public sealed class ScoreSyntaxFuncBodyEmpty(ScoreSyntaxToken semiColonToken)
    : ScoreSyntaxFuncBody(semiColonToken.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxFuncBodyEmpty);

    public ScoreSyntaxToken SemiColonToken { get; } = semiColonToken;
    public override IEnumerable<ITreeDebugNode> Children { get; } = [semiColonToken];
}

public sealed class ScoreSyntaxFuncBodyImplicitReturn(ScoreSyntaxToken equalToken, ScoreSyntaxExpr returnValue)
    : ScoreSyntaxFuncBody(returnValue.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxFuncBodyImplicitReturn);

    public ScoreSyntaxToken EqualToken { get; } = equalToken;
    public ScoreSyntaxExpr ReturnValue { get; } = returnValue;
    public override IEnumerable<ITreeDebugNode> Children { get; } = [equalToken, returnValue];
}

public sealed class ScoreSyntaxFuncBodyCompound(ScoreSyntaxExprCompound compound)
    : ScoreSyntaxFuncBody(compound.Range)
{
    public override string DebugNodeName { get; } = nameof(ScoreSyntaxFuncBodyCompound);

    public ScoreSyntaxExprCompound Compound { get; } = compound;
    public override IEnumerable<ITreeDebugNode> Children { get; } = [compound];
}
