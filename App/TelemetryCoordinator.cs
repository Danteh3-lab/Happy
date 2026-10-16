namespace HappyBot.App;

/// <summary>
/// Starts, stops and exports telemetry without blocking the UI thread.
/// Stopping waits up to 15 s for queued frames, and zipping a long session can
/// take seconds, so both run on the thread pool and report back through the
/// dispatcher.
/// </summary>
internal sealed class TelemetryCoordinator
{
    private readonly BotCore _bot;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<string, string> _toast;
    private readonly Action _statusChanged;
    private volatile bool _stopping;
    private volatile bool _exporting;

    public TelemetryCoordinator(BotCore bot, IUiDispatcher dispatcher, Action<string, string> toast, Action statusChanged)
    {
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _toast = toast ?? throw new ArgumentNullException(nameof(toast));
        _statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
    }

    public bool IsStopping => _stopping;

    public void Toggle(string label)
    {
        if (_stopping)
        {
            _toast("Telemetry is still saving. Try again in a moment.", "info");
            return;
        }

        if (_bot.Telemetry.Recording)
        {
            _stopping = true;
            _toast("Saving telemetry...", "info");
            _ = Task.Run(() =>
            {
                string message;
                string kind;
                try
                {
                    _bot.StopTelemetry();
                    message = "Telemetry saved locally.";
                    kind = "success";
                }
                catch (Exception ex)
                {
                    message = "Telemetry could not be saved: " + ex.Message;
                    kind = "error";
                }
                _dispatcher.Post(() =>
                {
                    _stopping = false;
                    _toast(message, kind);
                    _statusChanged();
                });
            });
        }
        else
        {
            _bot.StartTelemetry(label);
            _toast($"Telemetry recording: {label}.", "info");
        }
        _statusChanged();
    }

    /// <summary>Asks for a path on the UI thread (the dialog is modal), then zips in the background.</summary>
    public void Export(ISaveFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (_exporting)
        {
            _toast("An export is already running.", "info");
            return;
        }

        if (!_bot.TryGetLatestTelemetryPath(out string source))
        {
            _toast("No telemetry session has been recorded yet.", "info");
            return;
        }

        string target = picker.PickSavePath("Export DANBOT telemetry", "ZIP archive|*.zip", Path.GetFileName(source) + ".zip");
        if (string.IsNullOrEmpty(target))
        {
            _toast("Export cancelled.", "info");
            return;
        }

        _exporting = true;
        _toast("Exporting telemetry...", "info");
        _ = Task.Run(() =>
        {
            string message;
            string kind;
            try
            {
                TelemetryRecorder.ExportTo(source, target);
                message = "Telemetry ZIP exported.";
                kind = "success";
            }
            catch (Exception ex)
            {
                message = ex.Message;
                kind = "info";
            }
            _dispatcher.Post(() =>
            {
                _exporting = false;
                _toast(message, kind);
            });
        });
    }
}
