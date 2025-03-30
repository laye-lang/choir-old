namespace Choir.LanguageServer;

public abstract class BaseLanguageServer
{
    protected string? ReadJsonContent()
    {
        int? contentLength = null;
        string? contentType = null;

        while (true)
        {
            string? line = Console.In.ReadLine();
            if (line is null) return null;
            if (!string.IsNullOrEmpty(line))
            {
                if (!ProcessHeaderLine(line))
                    return null;
                continue;
            }

            if (!contentLength.HasValue)
            {
                Console.Error.WriteLine("No content length header");
                return null;
            }

            char[] contentData = new char[contentLength.Value];
            Console.In.ReadBlock(contentData.AsSpan());

            return new string(contentData);
        }

        bool ProcessHeaderLine(string header)
        {
            if (header.StartsWith("Content-Length: "))
            {
                if (!int.TryParse(header.AsSpan("Content-Length: ".Length), out int contentLengthParsed))
                    return false;

                contentLength = contentLengthParsed;
                return true;
            }
            else if (header.StartsWith("Content-Type: "))
            {
                contentType = header["Content-Type: ".Length..];
                return true;
            }

            return false;
        }
    }
}
