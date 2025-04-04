namespace Choir.Front.Laye.Syntax;

public sealed class SyntaxTargetCondition(SyntaxToken targetToken, SyntaxToken targetStringToken)
    : SyntaxNode(targetToken.Location)
{
    public SyntaxToken TargetToken { get; } = targetToken;
    public SyntaxToken TargetStringToken { get; } = targetStringToken;
    public override IEnumerable<SyntaxNode> Children { get; } = [targetToken, targetStringToken];
}

public abstract class SyntaxAttrib(Location location) : SyntaxNode(location);

public sealed class SyntaxAttribForeign(SyntaxToken tokenForeign, SyntaxToken? tokenName)
    : SyntaxAttrib(tokenForeign.Location)
{
    public SyntaxToken TokenForegin { get; } = tokenForeign;
    public SyntaxToken? TokenForeignLibraryName { get; init; } = null;
    public SyntaxToken? TokenName { get; } = tokenName;
    
    public bool HasForeignLibraryName => TokenForeignLibraryName is not null;
    public bool HasForeignName => TokenName is not null;
    public string? ForeignLibraryNameText => TokenForeignLibraryName?.TextValue;
    public string? ForeignNameText => TokenName?.TextValue;

    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            yield return TokenForegin;
            if (TokenForeignLibraryName is not null)
                yield return TokenForeignLibraryName;
            if (TokenName is not null)
                yield return TokenName;
        }
    }
}

public sealed class SyntaxAttribCallconv(SyntaxToken tokenCallconv, SyntaxToken tokenKind)
    : SyntaxAttrib(tokenCallconv.Location)
{
    public SyntaxToken TokenForegin { get; } = tokenCallconv;
    public SyntaxToken TokenKind { get; } = tokenKind;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenCallconv, tokenKind];
}

public sealed class SyntaxAttribExport(SyntaxToken tokenExport)
    : SyntaxAttrib(tokenExport.Location)
{
    public SyntaxToken TokenForegin { get; } = tokenExport;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenExport];
}

public sealed class SyntaxAttribInline(SyntaxToken tokenInline)
    : SyntaxAttrib(tokenInline.Location)
{
    public SyntaxToken TokenForegin { get; } = tokenInline;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenInline];
}

public sealed class SyntaxAttribDiscardable(SyntaxToken tokenDiscardable)
    : SyntaxAttrib(tokenDiscardable.Location)
{
    public SyntaxToken TokenForegin { get; } = tokenDiscardable;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenDiscardable];
}
