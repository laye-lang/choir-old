using Choir.Source;

namespace Choir.FrontEnd.C.Preprocess;

public sealed class CPPToken(CPPTokenKind kind, SourceRange range, CTriviaList leadingTrivia, CTriviaList trailingTrivia)
{
    public CPPTokenKind Kind { get; } = kind;
    // CPPTokens don't need to track their origin source text like a regular token would.
    // A CPPToken only exists in the context of a single source text, and the preprocessing transformation is what processing #include directives and introduces new source texts.
    // The result of the preprocessing step is a CSyntaxToken instead, which will track its source file.
    public SourceRange Range { get; } = range;
    public SourceLocation Location => Range.Begin;

    public bool IsAtStartOfLine { get; init; }

    public StringView StringValue { get; init; }

    public CTriviaList LeadingTrivia { get; } = leadingTrivia;
    public CTriviaList TrailingTrivia { get; } = trailingTrivia;
}
