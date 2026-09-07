namespace HappyBot;

/// <summary>
/// Source-independent health snapshot for a physical controller source.
/// Any future source (XInput, HID, virtual) reports the same shape so the
/// bridge and diagnostics UI never depend on a concrete device type.
/// </summary>
internal readonly record struct ControllerSourceDiagnostics(
    bool Connected,
    string Transport,
    string Fingerprint,
    long Reports,
    long StateChanges,
    long ParseErrors,
    long ReadErrors,
    long Reconnects,
    long LastReportAgeMs,
    long LastReportIntervalMs,
    long MaxReportIntervalMs);

/// <summary>
/// Lifecycle and diagnostics contract for a physical controller source.
/// Implementations publish normalized <see cref="ControllerState"/> values
/// through their constructor callback while the bridge remains responsible
/// for merging automation input and submitting the virtual pad report.
/// </summary>
internal interface IControllerSource : IDisposable
{
    bool IsRunning { get; }

    ControllerSourceDiagnostics Diagnostics { get; }

    void Start();

    void Stop();
}
