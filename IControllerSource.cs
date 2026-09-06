namespace HappyBot;

/// <summary>
/// Lifecycle and diagnostics contract for a physical controller source.
/// Implementations publish normalized <see cref="ControllerState"/> values
/// through their constructor callback while the bridge remains responsible
/// for merging automation input and submitting the virtual pad report.
/// </summary>
internal interface IControllerSource : IDisposable
{
    bool IsRunning { get; }

    Ds4SourceDiagnostics Diagnostics { get; }

    void Start();

    void Stop();
}
