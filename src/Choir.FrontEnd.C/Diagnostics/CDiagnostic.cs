using Choir.Source;

namespace Choir.FrontEnd.C.Diagnostics;

public static class CDiagnostic
{
    #region CC0XXX - Miscellaneous Tooling/Internal Diagnostics

    #endregion

    #region CC1XXX - Lexical Diagnostics

    public static void ErrorUnexpectedCharacter(this CCContext context, SourceText source, SourceLocation location) =>
        context.EmitDiagnostic(CDiagnosticSemantic.Error, "CC1001", source, location, [], "Unexpected character.");

    public static void ErrorUnclosedComment(this CCContext context, SourceText source, SourceLocation location) =>
        context.EmitDiagnostic(CDiagnosticSemantic.Error, "CC1002", source, location, [], "Unclosed comment.");

    public static void ErrorExpectedMatchingCloseDelimiter(this CCContext context, SourceText source, char openDelimiter, char closeDelimiter, SourceLocation closeLocation, SourceLocation openLocation)
    {
        context.EmitDiagnostic(CDiagnosticSemantic.Error, "CC1003", source, closeLocation, [], $"Expected a closing delimiter '{closeDelimiter}'...");
        context.EmitDiagnostic(CDiagnosticSemantic.Note, "CC1003", source, openLocation, [], $"... to match the opening '{openDelimiter}'.");
    }

    #endregion

    #region CC2XXX - Syntactic Diagnostics

    #endregion

    #region CC3XXX - Semantic Diagnostics

    #endregion
}
