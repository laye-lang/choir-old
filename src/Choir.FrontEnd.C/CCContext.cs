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
        { CDiagnosticSemantic.Extension, DiagnosticLevel.Ignore },
        { CDiagnosticSemantic.ExtensionWarning, DiagnosticLevel.Warning },
        { CDiagnosticSemantic.Error, DiagnosticLevel.Error },
    };

    public CPedanticMode PedanticMode { get; set; } = CPedanticMode.Normal;

    public CCContext(IDiagnosticConsumer diagConsumer, Target target)
        : base(diagConsumer, target)
    {
    }

    private DiagnosticLevel MapSemantic(CDiagnosticSemantic semantic, string id)
    {
        if (semantic is CDiagnosticSemantic.Extension)
        {
            if (PedanticMode is CPedanticMode.Warning)
                semantic = CDiagnosticSemantic.Warning;
            else if (PedanticMode is CPedanticMode.Error)
                semantic = CDiagnosticSemantic.Error;
        }
        else if (semantic is CDiagnosticSemantic.ExtensionWarning)
        {
            if (PedanticMode is CPedanticMode.Error)
                semantic = CDiagnosticSemantic.Error;
        }

        return _semanticLevels[semantic];
    }

    public Diagnostic EmitDiagnostic(CDiagnosticSemantic semantic, string id, SourceText source,
        SourceLocation location, SourceRange[] ranges, string message)
    {
        return Diag.Emit(MapSemantic(semantic, id), id, source, location, ranges, message);
    }

    public Diagnostic EmitDiagnostic(CDiagnosticSemantic semantic, string id, SourceText source,
        SourceLocation location, SourceRange[] ranges, Markup message)
    {
        return Diag.Emit(MapSemantic(semantic, id), id, source, location, ranges, message);
    }

    public Diagnostic EmitDiagnostic(CDiagnosticSemantic semantic, string id, SourceText source,
        SourceLocation location, SourceRange[] ranges, MarkupInterpolatedStringHandler message)
    {
        return Diag.Emit(MapSemantic(semantic, id), id, source, location, ranges, message.Markup);
    }
}
