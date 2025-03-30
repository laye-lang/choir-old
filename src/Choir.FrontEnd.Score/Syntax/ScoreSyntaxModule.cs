namespace Choir.FrontEnd.Score.Syntax;

public sealed class ScoreSyntaxModule(IReadOnlyList<ScoreSyntaxUnit> syntaxUnits)
{
    public IReadOnlyList<ScoreSyntaxUnit> SyntaxUnits { get; } = syntaxUnits;
}
