using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public sealed class ScoreSyntaxDebugVisualizer(SourceText source, bool useColor)
    : BaseTreeDebugVisualizer<ScoreSyntaxNode>(useColor)
{
    public void PrintTokens(IEnumerable<ScoreSyntaxToken> tokens)
    {
        SetColor(ColorBase);
        Console.WriteLine("Tokens");
        PrintChildren(tokens);
    }

    public void PrintSyntaxUnit(ScoreSyntaxUnit unit)
    {
        SetColor(ColorMisc);
        Console.WriteLine($"// Score Syntax Unit \"{unit.Source.Name}\"");
        Print(unit);
    }

    protected override void Print(ScoreSyntaxNode node)
    {
        SetColor(ColorBase);
        Console.Write($"{node.DebugNodeName} ");

        if (node is ScoreSyntaxToken token)
            PrintToken(token);
        else if (node is ScoreTriviaList triviaList)
            PrintTriviaList(triviaList);
        else if (node is ScoreTrivia trivia)
            PrintTrivia(trivia);

        Console.ResetColor();
        Console.WriteLine();

        PrintChildren(node.Children);
    }

    private void PrintToken(ScoreSyntaxToken token)
    {
        SetColor(ColorProperty);
        Console.Write(token.Kind);

        if (token.Kind is ScoreTokenKind.Identifier)
        {
            SetColor(ColorName);
            Console.Write(' ');
            Console.Write(token.StringValue);
        }
        else if (token.Kind is ScoreTokenKind.LiteralInteger or ScoreTokenKind.LiteralFloat or ScoreTokenKind.LiteralString)
        {
            SetColor(ColorValue);
            Console.Write(' ');
            Console.Write(token.GetSourceSlice(source));
        }
        else if (token.Range.Length <= 64)
        {
            string image = source.Substring(token.Range);
            if (!image.Contains('\r') && !image.Contains('\n'))
            {
                SetColor(ColorMisc);
                Console.Write(' ');
                Console.Write(image);
            }
        }
    }

    private void PrintTrivia(ScoreTrivia trivia)
    {
        if (trivia.Range.Length <= 64)
        {
            string image = source.Substring(trivia.Range);
            if (!image.Contains('\r') && !image.Contains('\n'))
            {
                SetColor(ColorMisc);
                if (string.IsNullOrWhiteSpace(image))
                    Console.Write($"'{image}'");
                else Console.Write(image);
            }
        }
    }

    private void PrintTriviaList(ScoreTriviaList triviaList)
    {
        SetColor(ColorProperty);
        Console.Write(triviaList.IsLeading ? "Leading" : "Trailing");
    }
}
