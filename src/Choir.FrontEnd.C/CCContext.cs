using Choir.Diagnostics;
using Choir.Formatting;
using Choir.FrontEnd.C.Diagnostics;
using Choir.Source;

namespace Choir.FrontEnd.C;

public sealed class CCContext
    : ChoirContext
{
    private readonly Dictionary<CDiagnosticSemantic, DiagnosticLevel> _semanticLevels = new()
    {
        { CDiagnosticSemantic.Note, DiagnosticLevel.Note },
        { CDiagnosticSemantic.Remark, DiagnosticLevel.Remark },
        { CDiagnosticSemantic.Warning, DiagnosticLevel.Warning },
        { CDiagnosticSemantic.Error, DiagnosticLevel.Error },
    };

    public CCContext(IDiagnosticConsumer diagConsumer, Target target)
        : base(diagConsumer, target)
    {
    }

    public Diagnostic EmitDiagnostic(CDiagnosticSemantic semantic, string id, SourceText source,
        SourceLocation location, SourceRange[] ranges, string message)
    {
        return Diag.Emit(_semanticLevels[semantic], id, source, location, ranges, message);
    }

    public Diagnostic EmitDiagnostic(CDiagnosticSemantic semantic, string id, SourceText source,
        SourceLocation location, SourceRange[] ranges, Markup message)
    {
        return Diag.Emit(_semanticLevels[semantic], id, source, location, ranges, message);
    }

    public Diagnostic EmitDiagnostic(CDiagnosticSemantic semantic, string id, SourceText source,
        SourceLocation location, SourceRange[] ranges, MarkupInterpolatedStringHandler message)
    {
        return Diag.Emit(_semanticLevels[semantic], id, source, location, ranges, message.Markup);
    }
}
