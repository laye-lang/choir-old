using System.Diagnostics.CodeAnalysis;

using Choir.Source;

namespace Choir.FrontEnd.C.Preprocess;

public sealed class CPPMacroDef(CToken nameToken,
    IReadOnlyList<CToken>? paramNames, IReadOnlyList<CToken> bodyTokens)
{
    public CToken NameToken { get; } = nameToken;
    public StringView Name { get; } = nameToken.StringValue;
    public bool HasParams { get; } = paramNames is not null;
    public IReadOnlyList<CToken> ParamNames { get; } = paramNames ?? [];
    public IReadOnlyList<CToken> Body { get; } = bodyTokens;
}

public sealed class CPPMacroStore
{
    private readonly Dictionary<StringView, CPPMacroDef> _defs = [];

    public CPPMacroDef? TryGetMacroDef(StringView macroName) => _defs.TryGetValue(macroName, out var macroDef) ? macroDef : null;
    public bool TryGetMacroDef(StringView macroName, [NotNullWhen(true)] out CPPMacroDef? macroDef) => _defs.TryGetValue(macroName, out macroDef);

    public void Define(CPPMacroDef macroDef) => _defs[macroDef.Name] = macroDef;
    public void Undefine(StringView name) => _defs.Remove(name);
}
