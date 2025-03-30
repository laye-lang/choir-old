using System.Diagnostics;
using System.Text.Json.Nodes;

using Choir.LanguageServer;

namespace Choir.FrontEnd.Score.LanguageServer;

public sealed class ScoreLanguageServer
    : BaseLanguageServer
{
    public int Run()
    {
#if DEBUG
        while (!Debugger.IsAttached)
        {
            Thread.Sleep(1000);
        }
#endif

        while (ReadJsonContent() is { } requestJson)
        {
            ;
            var jsonValue = JsonNode.Parse(requestJson);
            ;
        }

        return 0;
    }
}
