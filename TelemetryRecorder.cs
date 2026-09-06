using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
    private readonly object _sync = new();
    private volatile TelemetrySession _active;
    private TelemetrySession _last;
    private volatile bool _recording;

    /// <summary>
    /// All mutable state for one recording session. The writer holds its
    /// session for the whole drain, so a new session can start while the
    /// previous writer is still finishing without any shared counters.
    /// </summary>
    private sealed class TelemetrySession
    {
        public readonly string Path;
        public readonly string Label;
        public readonly Stopwatch Clock = new();
        public readonly BlockingCollection<TelemetryWorkItem> Queue;
        public readonly Dictionary<string, int> EventCounts = new(StringComparer.Ordinal);
        public readonly object Sync = new();
        public Task Writer;
        public long BytesWritten;
        public int Failures;
        public int Dropped;
        public long LastImageTick;

        public TelemetrySession(string path, string label, BlockingCollection<TelemetryWorkItem> queue)
        {
            Path = path;
            Label = label;
            Queue = queue;
        }

        public long ElapsedMs() => Clock.ElapsedMilliseconds;
    }

    public bool IsRecording
    {
        get => _recording;
    }

    /// <summary>Elapsed time in the active telemetry session.</summary>
    public long ElapsedMs
    {
        get
        {
            TelemetrySession session = _active ?? _last;
            return session?.ElapsedMs() ?? 0;
        }
    }

    public TelemetryStatus Status
    {
        get
        {
            TelemetrySession session;
            bool recording;
            lock (_sync)
            {
                session = _active ?? _last;
                recording = _recording;
            }
            if (session == null)
                return new TelemetryStatus(false, "", "", TimeSpan.Zero, 0, 0, 0,
                    new Dictionary<string, int>());
            lock (session.Sync)
            {
                return new TelemetryStatus(recording, session.Label, session.Path, session.Clock.Elapsed,
                    session.Failures, session.Dropped, Interlocked.Read(ref session.BytesWritten),
                    session.EventCounts.ToDictionary(x => x.Key, x => x.Value));
            }
        }
    }

    public void Start(string label)
    {
        TelemetrySession session;
        lock (_sync)
        {
            if (_recording) return;
            string safeLabel = SanitizeLabel(label);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            // Unique suffix: a quick stop/start with the same label must not
            // reuse the directory (events would append together and the new
            // summary would overwrite the old one).
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DANBOT", "Telemetry", stamp + "-" + safeLabel + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(path, "roi"));
            session = new TelemetrySession(path, safeLabel, CreateQueue());
            session.Clock.Restart();
            _active = session;
            _recording = true;
            session.Writer = Task.Run(() => WriteLoop(session.Queue, session));
        }
        Record("session-start", new { label = session.Label, format = 2 });
    }

    public void Stop()
    {
        TelemetrySession session;
        lock (_sync)
        {
            if (!_recording) return;
            session = _active;
        }
        RecordInternal(session, "session-stop", new { }, false);
        lock (_sync)
        {
            _recording = false;
            _active = null;
            _last = session;
        }
        // The writer disposes its queue on exit, so a writer that already
        // failed (e.g. events.jsonl unwritable) may have disposed it.
        try { session.Queue.CompleteAdding(); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        // The summary must reflect the fully drained queue (image events are
        // counted as they are written). Wait for the writer; on pathological
        // stalls write it from a continuation instead of racing the drain.
        // The summary reads only this session, so a new session starting
        // meanwhile cannot corrupt it.
        bool drained;
        try { drained = session.Writer == null || session.Writer.Wait(15000); }
        catch { drained = true; }
        session.Clock.Stop();
        if (drained)
        {
            WriteSummary(session);
        }
        else if (session.Writer != null)
        {
            try { session.Writer.ContinueWith(_ => WriteSummary(session), TaskScheduler.Default); } catch { }
        }
    }

    public void Record(string name, object payload, bool failure = false)
    {
        if (!_recording) return;
        TelemetrySession session = _active;
        if (session == null) return;
        RecordInternal(session, name, payload, failure);
    }

    public void CaptureRoi(string reason, Rectangle screenRegion)
    {
        if (!_recording) return;
        TelemetrySession session = _active;
        if (session == null || screenRegion.Width <= 0 || screenRegion.Height <= 0) return;
        lock (session.Sync)
        {
            if (!_recording || _active != session) return;
            long now = Environment.TickCount64;
            if (now - session.LastImageTick < ImageThrottleMs) return;
            session.LastImageTick = now;
        }
        TryEnqueue(session, new TelemetryImageWorkItem(reason, screenRegion, session.ElapsedMs()));
    }

    /// <summary>
    /// Queues a copy of an already-scanned frame. The copy is made on the vision
    /// loop so the asynchronous writer never observes a reused capture buffer.
    /// </summary>
    public bool CaptureFrameSnapshot(string attemptId, int scheduledOffsetMs, long capturedElapsedMs, ScreenFrame frame)
    {
        if (!_recording || frame == null || frame.Width <= 0 || frame.Height <= 0 || frame.Stride == 0 || frame.Buffer == null)
            return false;
        TelemetrySession session = _active;
        if (session == null) return false;

        int rowBytes = Math.Abs(frame.Stride);
        long requiredBytes = (long)rowBytes * frame.Height;
        if (requiredBytes <= 0 || requiredBytes > frame.Buffer.Length || requiredBytes > int.MaxValue) return false;

        if (Interlocked.Read(ref session.BytesWritten) >= SessionLimitBytes) return false;
        if (QueueFull(session)) return false;

        byte[] buffer = new byte[(int)requiredBytes];
        Buffer.BlockCopy(frame.Buffer, 0, buffer, 0, buffer.Length);
        var snapshot = new TelemetryFrameSnapshot(frame.Width, frame.Height, frame.Stride,
            frame.OriginX, frame.OriginY, buffer);
        return TryEnqueue(session, new TelemetryFrameWorkItem(SanitizeLabel(attemptId), scheduledOffsetMs,
            capturedElapsedMs, snapshot));
    }

    /// <summary>
    /// Queues an exact crop of the frame currently being processed. This is used
    /// for detector calibration: unlike CaptureRoi, it never performs a later
    /// screen capture on the writer thread.
    /// </summary>
    public string CaptureCalibrationRegionSnapshot(long candidateId, string stage, int clusterMatches,
        ScreenFrame frame, Rectangle screenRegion)
    {
        if (!_recording || frame == null || frame.Width <= 0 || frame.Height <= 0 ||
            frame.Stride == 0 || frame.Buffer == null)
            return "";

        Rectangle frameBounds = new(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        Rectangle region = Rectangle.Intersect(frameBounds, screenRegion);
        if (region.Width <= 0 || region.Height <= 0) return "";
        TelemetrySession session = _active;
        if (session == null) return "";

        if (Interlocked.Read(ref session.BytesWritten) >= SessionLimitBytes || QueueFull(session))
            return "";

        int sourceStride = Math.Abs(frame.Stride);
        int rowBytes = region.Width * 4;
        byte[] buffer = new byte[rowBytes * region.Height];
        int sourceX = region.Left - frame.OriginX;
        int sourceY = region.Top - frame.OriginY;
        for (int y = 0; y < region.Height; y++)
        {
            int sourceRow = frame.Stride >= 0 ? sourceY + y : frame.Height - 1 - (sourceY + y);
            Buffer.BlockCopy(frame.Buffer, sourceRow * sourceStride + sourceX * 4,
                buffer, y * rowBytes, rowBytes);
        }

        long capturedElapsedMs = session.ElapsedMs();
        string safeStage = SanitizeLabel(stage);
        string candidateDirectory = "candidate-" + candidateId.ToString("D6");
        string fileName = $"{capturedElapsedMs:D8}-{safeStage}.png";
        string relativePath = Path.Combine("flash-calibration", candidateDirectory, fileName)
            .Replace(Path.DirectorySeparatorChar, '/');
        var snapshot = new TelemetryFrameSnapshot(region.Width, region.Height, rowBytes,
            region.Left, region.Top, buffer);
        return TryEnqueue(session, new TelemetryCalibrationFrameWorkItem(candidateId, safeStage, clusterMatches,
            capturedElapsedMs, region, relativePath, snapshot)) ? relativePath : "";
    }

    /// <summary>
    /// Queues an exact crop of the frame currently being processed for a named
    /// detector event. Unlike CaptureRoi, this never performs a later screen
    /// recapture, so the image shows precisely what the detector saw.
    /// </summary>
    public string CaptureRegionSnapshot(string reason, ScreenFrame frame, Rectangle screenRegion)
    {
        if (!_recording || frame == null || frame.Width <= 0 || frame.Height <= 0 ||
            frame.Stride == 0 || frame.Buffer == null)
            return "";

        Rectangle frameBounds = new(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        Rectangle region = Rectangle.Intersect(frameBounds, screenRegion);
        if (region.Width <= 0 || region.Height <= 0) return "";
        TelemetrySession session = _active;
        if (session == null) return "";

        if (Interlocked.Read(ref session.BytesWritten) >= SessionLimitBytes || QueueFull(session))
            return "";

        int sourceStride = Math.Abs(frame.Stride);
        int rowBytes = region.Width * 4;
        byte[] buffer = new byte[rowBytes * region.Height];
        int sourceX = region.Left - frame.OriginX;
        int sourceY = region.Top - frame.OriginY;
        for (int y = 0; y < region.Height; y++)
        {
            int sourceRow = frame.Stride >= 0 ? sourceY + y : frame.Height - 1 - (sourceY + y);
            Buffer.BlockCopy(frame.Buffer, sourceRow * sourceStride + sourceX * 4,
                buffer, y * rowBytes, rowBytes);
        }

        long capturedElapsedMs = session.ElapsedMs();
        string safeReason = SanitizeLabel(reason);
        string fileName = $"{capturedElapsedMs:D8}-{Guid.NewGuid().ToString("N")[..8]}.png";
        string relativePath = Path.Combine("snapshots", safeReason, fileName)
            .Replace(Path.DirectorySeparatorChar, '/');
        var snapshot = new TelemetryFrameSnapshot(region.Width, region.Height, rowBytes,
            region.Left, region.Top, buffer);
        return TryEnqueue(session, new TelemetrySnapshotFrameWorkItem(safeReason, capturedElapsedMs,
            region, relativePath, snapshot)) ? relativePath : "";
    }

    public bool ExportLatest(IWin32Window owner, out string result)
    {
        string source;
        lock (_sync) source = (_active ?? _last)?.Path;
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

    private void RecordInternal(TelemetrySession session, string name, object payload, bool failure)
    {
        if (session == null) return;
        lock (session.Sync)
        {
            if (session.BytesWritten >= SessionLimitBytes) return;
            session.EventCounts[name] = session.EventCounts.GetValueOrDefault(name) + 1;
            if (failure) session.Failures++;
        }
        TryEnqueue(session, new TelemetryEventWorkItem(new TelemetryEvent(session.ElapsedMs(), DateTimeOffset.UtcNow, name, payload, failure)));
    }

    private bool TryEnqueue(TelemetrySession session, TelemetryWorkItem item)
    {
        // The session's queue can be completed and disposed by Stop()/writer
        // at any point during this call. Never let that race throw into the
        // combat loop; treat it as a dropped item.
        try
        {
            BlockingCollection<TelemetryWorkItem> queue = session.Queue;
            if (queue.IsAddingCompleted)
            {
                Interlocked.Increment(ref session.Dropped);
                return false;
            }
            if (!queue.TryAdd(item))
            {
                Interlocked.Increment(ref session.Dropped);
                return false;
            }
            return true;
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Increment(ref session.Dropped);
            return false;
        }
        catch (InvalidOperationException)
        {
            Interlocked.Increment(ref session.Dropped);
            return false;
        }
    }

    /// <summary>
    /// Best-effort capacity gate. A queue completed/disposed mid-read races
    /// with Stop(); treat either as full so the producer drops the snapshot.
    /// </summary>
    private static bool QueueFull(TelemetrySession session)
    {
        try
        {
            return session.Queue.Count >= QueueCapacity;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private void WriteLoop(BlockingCollection<TelemetryWorkItem> queue, TelemetrySession session)
    {
        string sessionPath = session.Path;
        string eventsPath = Path.Combine(sessionPath, "events.jsonl");
        try
        {
            // BOM-less: BytesWritten counts exactly the serialized lines and
            // newlines, so a preamble would break the 250 MB accounting.
            using var writer = new StreamWriter(eventsPath, append: true, Utf8NoBom, 65536);
            int bufferedLines = 0;
            foreach (TelemetryWorkItem item in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (item is TelemetryEventWorkItem eventItem)
                    {
                        string line = JsonSerializer.Serialize(eventItem.Event);
                        writer.WriteLine(line);
                        Interlocked.Add(ref session.BytesWritten, System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
                        if (++bufferedLines >= 50)
                        {
                            writer.Flush();
                            bufferedLines = 0;
                        }
                    }
                    else
                    {
                        writer.Flush();
                        bufferedLines = 0;
                        if (item is TelemetryImageWorkItem imageItem)
                        {
                            WriteImage(session, imageItem, sessionPath, writer, ref bufferedLines);
                        }
                        else if (item is TelemetryFrameWorkItem frameItem)
                        {
                            WriteFrame(session, frameItem, sessionPath, writer, ref bufferedLines);
                        }
                        else if (item is TelemetryCalibrationFrameWorkItem calibrationItem)
                        {
                            WriteCalibrationFrame(session, calibrationItem, sessionPath, writer, ref bufferedLines);
                        }
                        else if (item is TelemetrySnapshotFrameWorkItem snapshotItem)
                        {
                            WriteSnapshotFrame(session, snapshotItem, sessionPath, writer, ref bufferedLines);
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref session.Dropped);
                }
            }
        }
        catch
        {
            Interlocked.Increment(ref session.Dropped);
        }
        finally
        {
            // The writer owns its queue: a new session may have started with a
            // fresh queue while this writer was still draining the old one.
            try { queue.Dispose(); } catch { }
        }
    }

    private void AppendEventLine(TelemetrySession session, StreamWriter writer, ref int bufferedLines, string line)
    {
        writer.WriteLine(line);
        Interlocked.Add(ref session.BytesWritten, System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
        if (++bufferedLines >= 50)
        {
            writer.Flush();
            bufferedLines = 0;
        }
    }

    private void WriteImage(TelemetrySession session, TelemetryImageWorkItem item, string sessionPath, StreamWriter writer, ref int bufferedLines)
    {
        if (Interlocked.Read(ref session.BytesWritten) >= SessionLimitBytes) return;
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        Rectangle region = Rectangle.Intersect(bounds, item.Region);
        if (region.Width <= 0 || region.Height <= 0) return;

        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        string file = Path.Combine(sessionPath, "roi", $"{item.ElapsedMs:D8}-{SanitizeLabel(item.Reason)}.png");
        bitmap.Save(file, ImageFormat.Png);
        Interlocked.Add(ref session.BytesWritten, new FileInfo(file).Length);
        var imageEvent = new TelemetryEvent(item.ElapsedMs, DateTimeOffset.UtcNow, "roi-image",
            new { item.Reason, item.ElapsedMs, region = new { region.X, region.Y, region.Width, region.Height }, file = Path.GetFileName(file) }, false);
        AppendEventLine(session, writer, ref bufferedLines, JsonSerializer.Serialize(imageEvent));
        lock (session.Sync) session.EventCounts["roi-image"] = session.EventCounts.GetValueOrDefault("roi-image") + 1;
    }

    private void WriteFrame(TelemetrySession session, TelemetryFrameWorkItem item, string sessionPath, StreamWriter writer, ref int bufferedLines)
    {
        if (Interlocked.Read(ref session.BytesWritten) >= SessionLimitBytes) return;
        string attemptDirectory = Path.Combine(sessionPath, "parry-evidence", item.AttemptId);
        Directory.CreateDirectory(attemptDirectory);
        string fileName = $"{item.ScheduledOffsetMs:D4}ms-{item.CapturedElapsedMs:D8}.png";
        string file = Path.Combine(attemptDirectory, fileName);

        using (Bitmap bitmap = BitmapFromFrame(item.Frame))
            bitmap.Save(file, ImageFormat.Png);
        Interlocked.Add(ref session.BytesWritten, new FileInfo(file).Length);

        var imageEvent = new TelemetryEvent(item.CapturedElapsedMs, DateTimeOffset.UtcNow, "parry-evidence-frame",
            new
            {
                attemptId = item.AttemptId,
                scheduledOffsetMs = item.ScheduledOffsetMs,
                capturedElapsedMs = item.CapturedElapsedMs,
                timestampMs = item.CapturedElapsedMs,
                frame = new { width = item.Frame.Width, height = item.Frame.Height, originX = item.Frame.OriginX, originY = item.Frame.OriginY },
                file = Path.Combine("parry-evidence", item.AttemptId, fileName).Replace(Path.DirectorySeparatorChar, '/')
            }, false);
        AppendEventLine(session, writer, ref bufferedLines, JsonSerializer.Serialize(imageEvent));
        lock (session.Sync) session.EventCounts["parry-evidence-frame"] = session.EventCounts.GetValueOrDefault("parry-evidence-frame") + 1;
    }

    private void WriteCalibrationFrame(TelemetrySession session, TelemetryCalibrationFrameWorkItem item, string sessionPath, StreamWriter writer, ref int bufferedLines)
    {
        if (Interlocked.Read(ref session.BytesWritten) >= SessionLimitBytes) return;
        string relativeDirectory = Path.GetDirectoryName(item.RelativePath) ?? "flash-calibration";
        string directory = Path.Combine(sessionPath, relativeDirectory);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(sessionPath, item.RelativePath);

        using (Bitmap bitmap = BitmapFromFrame(item.Frame))
            bitmap.Save(file, ImageFormat.Png);
        Interlocked.Add(ref session.BytesWritten, new FileInfo(file).Length);

        var imageEvent = new TelemetryEvent(item.CapturedElapsedMs, DateTimeOffset.UtcNow, "flash-calibration-frame",
            new
            {
                candidateId = item.CandidateId,
                stage = item.Stage,
                clusterMatches = item.ClusterMatches,
                capturedElapsedMs = item.CapturedElapsedMs,
                timestampMs = item.CapturedElapsedMs,
                region = new { item.Region.X, item.Region.Y, item.Region.Width, item.Region.Height },
                file = item.RelativePath
            }, false);
        AppendEventLine(session, writer, ref bufferedLines, JsonSerializer.Serialize(imageEvent));
        lock (session.Sync) session.EventCounts["flash-calibration-frame"] = session.EventCounts.GetValueOrDefault("flash-calibration-frame") + 1;
    }

    private void WriteSnapshotFrame(TelemetrySession session, TelemetrySnapshotFrameWorkItem item, string sessionPath, StreamWriter writer, ref int bufferedLines)
    {
        if (Interlocked.Read(ref session.BytesWritten) >= SessionLimitBytes) return;
        string file = Path.Combine(sessionPath, item.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? sessionPath);

        using (Bitmap bitmap = BitmapFromFrame(item.Frame))
            bitmap.Save(file, ImageFormat.Png);
        Interlocked.Add(ref session.BytesWritten, new FileInfo(file).Length);

        var imageEvent = new TelemetryEvent(item.CapturedElapsedMs, DateTimeOffset.UtcNow,
            "telemetry-snapshot", new
            {
                reason = item.Reason,
                capturedElapsedMs = item.CapturedElapsedMs,
                timestampMs = item.CapturedElapsedMs,
                region = new { item.Region.X, item.Region.Y, item.Region.Width, item.Region.Height },
                file = item.RelativePath
            }, false);
        AppendEventLine(session, writer, ref bufferedLines, JsonSerializer.Serialize(imageEvent));
        lock (session.Sync) session.EventCounts["telemetry-snapshot"] = session.EventCounts.GetValueOrDefault("telemetry-snapshot") + 1;
    }

    private static Bitmap BitmapFromFrame(TelemetryFrameSnapshot frame)
    {
        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int sourceStride = Math.Abs(frame.Stride);
            int destinationStride = Math.Abs(data.Stride);
            int rowBytes = Math.Min(frame.Width * 4, Math.Min(sourceStride, destinationStride));
            for (int y = 0; y < frame.Height; y++)
            {
                int sourceRow = frame.Stride >= 0 ? y : frame.Height - 1 - y;
                Marshal.Copy(frame.Buffer, sourceRow * sourceStride,
                    IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private void WriteSummary(TelemetrySession session)
    {
        string path = session.Path;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        // Snapshot this session only: a newer session may already be
        // recording, and its counters must never leak into this summary.
        Dictionary<string, int> eventCounts;
        int failures;
        lock (session.Sync)
        {
            eventCounts = session.EventCounts.ToDictionary(x => x.Key, x => x.Value);
            failures = session.Failures;
        }
        long bytesWritten = Interlocked.Read(ref session.BytesWritten);
        int dropped = session.Dropped;
        TimeSpan duration = session.Clock.Elapsed;
        var summary = new
        {
            label = session.Label,
            startedLocal = Path.GetFileName(path),
            durationMs = (long)duration.TotalMilliseconds,
            failureCount = failures,
            droppedItems = dropped,
            bytesWritten = bytesWritten,
            eventCounts = eventCounts,
            diagnosticTotals = new
            {
                guardExpiredWhileWaiting = eventCounts.GetValueOrDefault("guard-expired-waiting"),
                longFlashWaits = eventCounts.GetValueOrDefault("wait-flash-500ms"),
                anchorJumps = eventCounts.GetValueOrDefault("anchor-jump"),
                markerLosses = eventCounts.GetValueOrDefault("marker-lost"),
                boxFlips = eventCounts.GetValueOrDefault("box-flip"),
                unknownDirections = eventCounts.GetValueOrDefault("indicator-unknown"),
                parryAttempts = eventCounts.GetValueOrDefault("parry-sent"),
                parryEvidenceFrames = eventCounts.GetValueOrDefault("parry-evidence-frame"),
                parryEvidenceCoalesced = eventCounts.GetValueOrDefault("parry-evidence-coalesced"),
                parryConfirmationBaselines = eventCounts.GetValueOrDefault("parry-confirmation-baseline"),
                parryConfirmationScans = eventCounts.GetValueOrDefault("parry-confirmation-scan"),
                parryConfirmationResults = eventCounts.GetValueOrDefault("parry-confirmation-result"),
                orangeParryDetections = eventCounts.GetValueOrDefault("orange-parry-detected"),
                orangeFeintGraceUsed = eventCounts.GetValueOrDefault("orange-feint-grace-used"),
                orangeFeintGraceExpired = eventCounts.GetValueOrDefault("orange-feint-grace-expired"),
                flashCalibrationFrames = eventCounts.GetValueOrDefault("flash-calibration-frame"),
                flashCalibrationResults = eventCounts.GetValueOrDefault("flash-calibration-result")
            }
        };
        File.WriteAllText(Path.Combine(path, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SanitizeLabel(string value)
    {
        string safe = new string((value ?? "Other").Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(safe) ? "Other" : safe[..Math.Min(safe.Length, 32)];
    }

    public void Dispose()
    {
        Stop();
        // Each writer disposes its own queue on exit; only reclaim the latest
        // here when no writer can still be draining it.
        TelemetrySession session;
        lock (_sync) session = _last;
        if (session != null && (session.Writer == null || session.Writer.IsCompleted))
        {
            try { session.Queue.Dispose(); } catch { }
        }
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
internal sealed record TelemetryCalibrationFrameWorkItem(long CandidateId, string Stage, int ClusterMatches,
    long CapturedElapsedMs, Rectangle Region, string RelativePath, TelemetryFrameSnapshot Frame) : TelemetryWorkItem;
internal sealed record TelemetrySnapshotFrameWorkItem(string Reason, long CapturedElapsedMs,
    Rectangle Region, string RelativePath, TelemetryFrameSnapshot Frame) : TelemetryWorkItem;
internal sealed record TelemetryFrameWorkItem(string AttemptId, int ScheduledOffsetMs, long CapturedElapsedMs,
    TelemetryFrameSnapshot Frame) : TelemetryWorkItem;
internal sealed record TelemetryFrameSnapshot(int Width, int Height, int Stride, int OriginX, int OriginY,
    byte[] Buffer);
