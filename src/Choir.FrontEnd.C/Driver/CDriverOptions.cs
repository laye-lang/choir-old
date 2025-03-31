using Choir.Diagnostics;
using Choir.Driver;

namespace Choir.FrontEnd.C.Driver;

public enum CCompilerCommand
{
    Compile,
    Run,
    Format,
    LanguageServer,
}

public sealed class CDriverOptions
    : BaseCompilerDriverOptions<CDriverOptions, CCContext, BaseCompilerDriverParseState>
{
    public CCompilerCommand Command { get; set; } = CCompilerCommand.Compile;

    public List<(string Name, FileInfo File)> InputFiles { get; set; } = [];

    protected override void HandleValue(string value, DiagnosticEngine diag, CliArgumentIterator args, BaseCompilerDriverParseState state)
    {
        var inputFile = new FileInfo(value);
        if (!inputFile.Exists)
            diag.Emit(DiagnosticLevel.Error, $"No such file or directory '{value}'.");

        InputFiles.Add((value, inputFile));
    }

    protected override void HandleArgument(string arg, DiagnosticEngine diag, CliArgumentIterator args, BaseCompilerDriverParseState state)
    {
        switch (arg)
        {
            default: base.HandleArgument(arg, diag, args, state); break;

            case "--run": Command = CCompilerCommand.Run; break;
            case "--format": Command = CCompilerCommand.Format; break;
            case "--lsp" or "--language-server": Command = CCompilerCommand.LanguageServer; break;
        }
    }
}
