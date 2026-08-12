using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;

namespace HappyBot;

/// <summary>
/// Opt-in, local-only diagnostics. It accepts small event records from the reaction
/// thread and writes them asynchronously so it cannot block guard decisions.
/// </summary>
public sealed class TelemetryRecorder : IDisposable
{
    private const int QueueCapacity = 512;
    private const long SessionLimitBytes = 250L * 1024 * 1024;
    private const int ImageThrottleMs = 250;
    private BlockingCollection<TelemetryWorkItem> _queue = CreateQueue();
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = new();
    private Task _writer;
    private string _sessionPath = "";
    private string _label = "";
    private long _bytesWritten;
    private long _lastImageTick;
    private int _dropped;
    private int _failures;
    private volatile bool _recording;

    public bool IsRecording
    {
        get => _recording;
    }

    public TelemetryStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new TelemetryStatus(_recording, _label, _sessionPath, _clock.Elapsed,
                    _failures, _dropped, _bytesWritten, _eventCounts.ToDictionary(x => x.Key, x => x.Value));
            }
        }
    }

    public void Start(string label)
    {
        BlockingCollection<TelemetryWorkItem> queue;
        string path;
        lock (_sync)
        {
            if (_recording) return;
            if (_queue.IsAddingCompleted)
            {
                _queue.Dispose();
                _queue = CreateQueue();
            }
            _label = SanitizeLabel(label);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _sessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DANBOT", "Telemetry", stamp + "-" + _label);
            Directory.CreateDirectory(Path.Combine(_sessionPath, "roi"));
            _eventCounts.Clear();
            _bytesWritten = 0;
            _lastImageTick = 0;
            _dropped = 0;
            _failures = 0;
            _clock.Restart();
            _recording = true;
            queue = _queue;
            path = _sessionPath;
            _writer = Task.Run(() => WriteLoop(queue, path));
        }
        Record("session-start", new { label = _label, format = 1 });
    }

    public void Stop()
    {
        string path;
        BlockingCollection<TelemetryWorkItem> queue;
        Task writer;
        lock (_sync)
        {
            if (!_recording) return;
            path = _sessionPath;
            queue = _queue;
            writer = _writer;
        }
        RecordInternal("session-stop", new { }, false);
        lock (_sync) _recording = false;
        queue.CompleteAdding();
        try { writer?.Wait(3000); } catch { }
        _clock.Stop();
        WriteSummary(path);
    }

    public void Record(string name, object payload, bool failure = false)
    {
        if (!_recording) return;
        lock (_sync)
        {
            if (!_recording) return;
        }
        RecordInternal(name, payload, failure);
    }

    public void CaptureRoi(string reason, Rectangle screenRegion)
    {
        if (!_recording) return;
        lock (_sync)
        {
            if (!_recording || screenRegion.Width <= 0 || screenRegion.Height <= 0) return;
            long now = Environment.TickCount64;
            if (now - _lastImageTick < ImageThrottleMs) return;
            _lastImageTick = now;
        }
        TryEnqueue(new TelemetryImageWorkItem(reason, screenRegion, ElapsedMilliseconds()));
    }

    public bool ExportLatest(IWin32Window owner, out string result)
    {
        string source;
        lock (_sync) source = _sessionPath;
        if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
        {
            result = "No telemetry session has been recorded yet.";
            return false;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export DANBOT telemetry",
            Filter = "ZIP archive|*.zip",
            FileName = Path.GetFileName(source) + ".zip",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            result = "Export cancelled.";
            return false;
        }

        try
        {
            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
            ZipFile.CreateFromDirectory(source, dialog.FileName, CompressionLevel.Fastest, false);
            result = dialog.FileName;
            return true;
        }
        catch (Exception ex)
        {
            result = ex.Message;
            return false;
        }
    }

    private void RecordInternal(string name, object payload, bool failure)
    {
        lock (_sync)
        {
            if (_bytesWritten >= SessionLimitBytes) return;
            _eventCounts[name] = _eventCounts.GetValueOrDefault(name) + 1;
            if (failure) _failures++;
        }
        TryEnqueue(new TelemetryEventWorkItem(new TelemetryEvent(ElapsedMilliseconds(), DateTimeOffset.UtcNow, name, payload, failure)));
    }

    private void TryEnqueue(TelemetryWorkItem item)
    {
        BlockingCollection<TelemetryWorkItem> queue;
        lock (_sync) queue = _queue;
        if (!queue.IsAddingCompleted && !queue.TryAdd(item)) Interlocked.Increment(ref _dropped);
    }

    private void WriteLoop(BlockingCollection<TelemetryWorkItem> queue, string sessionPath)
    {
        string eventsPath = Path.Combine(sessionPath, "events.jsonl");
        foreach (TelemetryWorkItem item in queue.GetConsumingEnumerable())
        {
            try
            {
                if (item is TelemetryEventWorkItem eventItem)
                {
                    string line = JsonSerializer.Serialize(eventItem.Event) + Environment.NewLine;
                    File.AppendAllText(eventsPath, line);
                    Interlocked.Add(ref _bytesWritten, System.Text.Encoding.UTF8.GetByteCount(line));
                }
                else if (item is TelemetryImageWorkItem imageItem)
                {
                    WriteImage(imageItem, sessionPath, eventsPath);
                }
            }
            catch
            {
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    private void WriteImage(TelemetryImageWorkItem item, string sessionPath, string eventsPath)
    {
        if (Interlocked.Read(ref _bytesWritten) >= SessionLimitBytes) return;
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        Rectangle region = Rectangle.Intersect(bounds, item.Region);
        if (region.Width <= 0 || region.Height <= 0) return;

        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        string file = Path.Combine(sessionPath, "roi", $"{item.ElapsedMs:D8}-{SanitizeLabel(item.Reason)}.png");
        bitmap.Save(file, ImageFormat.Png);
        Interlocked.Add(ref _bytesWritten, new FileInfo(file).Length);
        var imageEvent = new TelemetryEvent(item.ElapsedMs, DateTimeOffset.UtcNow, "roi-image",
            new { item.Reason, item.ElapsedMs, region = new { region.X, region.Y, region.Width, region.Height }, file = Path.GetFileName(file) }, false);
        string line = JsonSerializer.Serialize(imageEvent) + Environment.NewLine;
        File.AppendAllText(eventsPath, line);
        Interlocked.Add(ref _bytesWritten, System.Text.Encoding.UTF8.GetByteCount(line));
        lock (_sync) _eventCounts["roi-image"] = _eventCounts.GetValueOrDefault("roi-image") + 1;
    }

    private void WriteSummary(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        TelemetryStatus status = Status;
        var summary = new
        {
            label = status.Label,
            startedLocal = Path.GetFileName(path),
            durationMs = (long)status.Duration.TotalMilliseconds,
            failureCount = status.Failures,
            droppedItems = status.DroppedItems,
            bytesWritten = status.BytesWritten,
            eventCounts = status.EventCounts,
            diagnosticTotals = new
            {
                guardExpiredWhileWaiting = status.EventCounts.GetValueOrDefault("guard-expired-waiting"),
                longFlashWaits = status.EventCounts.GetValueOrDefault("wait-flash-500ms"),
                anchorJumps = status.EventCounts.GetValueOrDefault("anchor-jump"),
                markerLosses = status.EventCounts.GetValueOrDefault("marker-lost"),
                boxFlips = status.EventCounts.GetValueOrDefault("box-flip"),
                unknownDirections = status.EventCounts.GetValueOrDefault("indicator-unknown")
            }
        };
        File.WriteAllText(Path.Combine(path, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    private long ElapsedMilliseconds()
    {
        lock (_sync) return _clock.ElapsedMilliseconds;
    }

    private static string SanitizeLabel(string value)
    {
        string safe = new string((value ?? "Other").Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(safe) ? "Other" : safe[..Math.Min(safe.Length, 32)];
    }

    public void Dispose()
    {
        Stop();
        _queue.Dispose();
    }

    private static BlockingCollection<TelemetryWorkItem> CreateQueue() =>
        new(new ConcurrentQueue<TelemetryWorkItem>(), QueueCapacity);
}

public sealed record TelemetryStatus(bool Recording, string Label, string SessionPath, TimeSpan Duration,
    int Failures, int DroppedItems, long BytesWritten, IReadOnlyDictionary<string, int> EventCounts);

public sealed record TelemetryEvent(long ElapsedMs, DateTimeOffset Utc, string Name, object Data, bool Failure);

internal abstract record TelemetryWorkItem;
internal sealed record TelemetryEventWorkItem(TelemetryEvent Event) : TelemetryWorkItem;
internal sealed record TelemetryImageWorkItem(string Reason, Rectangle Region, long ElapsedMs) : TelemetryWorkItem;
