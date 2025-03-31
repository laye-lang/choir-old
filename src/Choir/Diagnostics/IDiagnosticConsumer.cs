namespace Choir.Diagnostics;

public delegate IDiagnosticConsumer DiagnosticConsumerProvider(bool useColor);

public interface IDiagnosticConsumer
    : IDisposable
{
    void IDisposable.Dispose() => GC.SuppressFinalize(this);

    public void Consume(Diagnostic diag);
    public void Flush();
}
