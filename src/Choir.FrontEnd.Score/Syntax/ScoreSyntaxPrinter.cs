using System.Text;

namespace Choir.FrontEnd.Score.Syntax;

public sealed class ScoreSyntaxPrinter
{
    public static string PrintToString(ScoreSyntaxUnit unit)
    {
        var printer = new ScoreSyntaxPrinter(unit);
        return printer.PrintToString();
    }

    private readonly StringBuilder _builder = new();
    private readonly ScoreSyntaxUnit _unit;

    private ScoreSyntaxPrinter(ScoreSyntaxUnit unit)
    {
        _unit = unit;
    }

    private string PrintToString()
    {
        _builder.Clear();
        foreach (var node in _unit.TopLevelNodes)
            PrintNode(node);

        return _builder.ToString();
    }

    private void PrintNode(ScoreSyntaxNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is ScoreSyntaxToken token)
                PrintToken(token);
            else PrintNode(child);
        }
    }

    private void PrintToken(ScoreSyntaxToken token)
    {
        PrintTrivia(token.LeadingTrivia);
        _builder.Append(_unit.Source.Substring(token.Range));
        PrintTrivia(token.TrailingTrivia);
    }

    private void PrintTrivia(ScoreTriviaList leadingTrivia)
    {
        foreach (var trivia in leadingTrivia.Trivia)
            _builder.Append(_unit.Source.Substring(trivia.Range));
    }
}
