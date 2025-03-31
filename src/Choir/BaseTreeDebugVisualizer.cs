using System.Text;

namespace Choir;

public interface ITreeDebugNode
{
    public string DebugNodeName { get; }
    public IEnumerable<ITreeDebugNode> Children { get; }
}

public abstract class BaseTreeDebugVisualizer(bool useColor)
{
    protected readonly StringBuilder _leadingText = new(128);

    protected ConsoleColor ColorBase = ConsoleColor.White;
    protected ConsoleColor ColorMisc = ConsoleColor.Gray;
    protected ConsoleColor ColorLocation = ConsoleColor.Magenta;
    protected ConsoleColor ColorName = ConsoleColor.Red;
    protected ConsoleColor ColorProperty = ConsoleColor.Blue;
    protected ConsoleColor ColorValue = ConsoleColor.Yellow;
    protected ConsoleColor ColorKeyword = ConsoleColor.Cyan;

    protected void SetColor(ConsoleColor color)
    {
        if (useColor) Console.ForegroundColor = color;
    }

    protected abstract void Print(ITreeDebugNode node);

    protected virtual void PrintChildren(IEnumerable<ITreeDebugNode> children)
    {
        if (!children.Any()) return;

        int leadingLength = _leadingText.Length;
        string currentLeading = _leadingText.ToString();

        _leadingText.Append("│ ");
        foreach (var child in children.Take(children.Count() - 1))
        {
            SetColor(ColorBase);
            Console.Write($"{currentLeading}├─");
            Print(child);
        }

        _leadingText.Length = leadingLength;
        SetColor(ColorBase);
        Console.Write($"{_leadingText}└─");

        _leadingText.Append("  ");
        Print(children.Last());

        _leadingText.Length = leadingLength;
    }
}
