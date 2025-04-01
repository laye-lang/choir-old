using System.Diagnostics.CodeAnalysis;

namespace Choir.FrontEnd.C.Preprocess;

public sealed class CPPMacroDef(StringView name,
    IReadOnlyList<CSyntaxToken>? paramNames, IReadOnlyList<CSyntaxToken> bodyTokens)
{
    public StringView Name { get; } = name;
    public bool HasParams { get; } = paramNames is not null;
    public IReadOnlyList<CSyntaxToken> ParamNames { get; } = paramNames ?? [];
    public IReadOnlyList<CSyntaxToken> Body { get; } = bodyTokens;
}

public sealed class CPPMacroStore
{
    private readonly Dictionary<StringView, CPPMacroDef> _defs = [];

    public CPPMacroDef? TryGetMacroDef(StringView macroName) => _defs.TryGetValue(macroName, out var macroDef) ? macroDef : null;
    public bool TryGetMacroDef(StringView macroName, [NotNullWhen(true)] out CPPMacroDef? macroDef) => _defs.TryGetValue(macroName, out macroDef);
}
