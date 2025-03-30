using Choir.Diagnostics;
using Choir.Driver;
using Choir.FrontEnd.Score.LanguageServer;
using Choir.FrontEnd.Score.Syntax;
using Choir.Source;

namespace Choir.FrontEnd.Score.Driver;

public delegate IDiagnosticConsumer DiagnosticConsumerProvider(bool useColor);

public sealed class ScoreDriver
    : ICompilerDriver
{
    public static int RunWithArgs(DiagnosticConsumerProvider diagProvider, string[] args, string programName = "score")
    {
        ScoreDriverOptions options;
        using (var parserDiag = diagProvider(true))
        using (var diag = new DiagnosticEngine(parserDiag))
        {
            options = ScoreDriverOptions.Parse(diag, new(args));
            if (diag.HasEmittedErrors)
                return 1;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine("TODO: Show Score compiler version text");
            return 0;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine("TODO: Show Score compiler help text");
            return 0;
        }

        using var driverDiag = diagProvider(options.OutputColoring);
        var driver = Create(driverDiag, options, programName);
        return driver.Execute();
    }

    public static ScoreDriver Create(IDiagnosticConsumer diagConsumer, ScoreDriverOptions options, string programName = "score")
    {
        return new ScoreDriver(programName, diagConsumer, options);
    }

    public string ProgramName { get; set; }
    public ScoreContext Context { get; set; }
    public ScoreDriverOptions Options { get; set; }

    private ScoreDriver(string programName, IDiagnosticConsumer diagConsumer, ScoreDriverOptions options)
    {
        ProgramName = programName;
        Context = new ScoreContext(diagConsumer, Target.X86_64);
        Options = options;
    }

    public int Execute()
    {
        if (Options.Command == ScoreCompilerCommand.LanguageServer)
            return LanguageServer();

        if (Options.Command == ScoreCompilerCommand.Format)
            return CommandFormat();

        foreach (var (fileName, file) in Options.InputFiles)
        {
            var source = new SourceText(fileName, File.ReadAllText(file.FullName));
            var printer = new ScoreSyntaxDebugVisualizer(source, Options.OutputColoring);

            var syntaxUnit = ScoreParser.ParseSyntaxUnit(Context, source);
            printer.PrintSyntaxUnit(syntaxUnit);
        }

        return 0;
    }

    private int LanguageServer()
    {
        var ls = new ScoreLanguageServer();
        return ls.Run();
    }

    private int CommandFormat()
    {
        foreach (var (fileName, file) in Options.InputFiles)
        {
            var source = new SourceText(fileName, File.ReadAllText(file.FullName));
            var syntaxUnit = ScoreParser.ParseSyntaxUnit(Context, source);
            string unitFormattedText = ScoreSyntaxPrinter.PrintToString(ScoreSyntaxFormatter.Format(syntaxUnit));
            Console.WriteLine(unitFormattedText);
        }

        return 0;
    }
}
