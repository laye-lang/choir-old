using System.Text;

using Choir.Diagnostics;
using Choir.FrontEnd.C.Driver;

namespace Choir.FrontEnd.C;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        return CDriver.RunWithArgs(useColor => new FormattedDiagnosticWriter(Console.Out, useColor), args);
    }
}
