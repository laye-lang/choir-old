using Choir.Source;

namespace Choir.FrontEnd.C.Preprocess;

public sealed class CToken(CTokenKind kind, SourceText source, SourceRange range, CTriviaList leadingTrivia, CTriviaList trailingTrivia)
{
    public CTokenKind Kind { get; } = kind;

    public SourceText Source { get; } = source;
    public SourceRange Range { get; } = range;
    public SourceLocation Location { get; } = range.Begin;

    public bool IsAtStartOfLine { get; init; }

    public CTriviaList LeadingTrivia { get; } = leadingTrivia;
    public CTriviaList TrailingTrivia { get; } = trailingTrivia;

    public StringView StringValue { get; init; }

    public bool IsMacroParam { get; set; }
    public int MacroParamIndex { get; set; }
}
