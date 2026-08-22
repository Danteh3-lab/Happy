using System.Drawing;
using HappyBot.Combat;

namespace HappyBot.Vision;

internal enum ParryConfirmationResult
{
    None,
    Confirmed,
    Unconfirmed
}

/// <summary>One scan result emitted by the visual parry confirmation tracker.</summary>
internal sealed record ParryConfirmationScan(
    string AttemptId,
    long CandidateId,
    CombatDirection Direction,
    long ElapsedMs,
    int BrightPixels,
    int Baseline,
    int Threshold,
    bool BaselineEstablished,
    bool BaselineEstablishedNow,
    bool Qualifying,
    int ConsecutiveQualifying,
    Rectangle Region,
    ParryConfirmationResult Result);

/// <summary>
/// Tracks visual proof after a delivered RT. It intentionally knows nothing
/// about telemetry or input delivery, so confirmation remains available when
/// telemetry recording is disabled.
/// </summary>
internal sealed class ParryConfirmationTracker
{
    internal const long ConfirmationStartMs = 150;
    internal const long ConfirmationEndMs = 650;
    internal const int MinimumBaseThreshold = 400;
    internal const int BaselineDelta = 250;
    internal const int ReferenceWidth = 1920;
    internal const int ReferenceHeight = 1200;

    private readonly object _sync = new();
    private readonly List<Attempt> _attempts = new();

    public bool HasPending
    {
        get { lock (_sync) return _attempts.Count > 0; }
    }

    public void Start(string attemptId, long candidateId, CombatDirection direction, long sentTick)
    {
        if (string.IsNullOrWhiteSpace(attemptId)) return;
        lock (_sync)
        {
            _attempts.RemoveAll(x => x.AttemptId == attemptId);
            _attempts.Add(new Attempt(attemptId, candidateId, direction, sentTick));
        }
    }

    public IReadOnlyList<ParryConfirmationScan> Scan(ScreenFrame frame, long nowTick, Rectangle screenBounds = default)
    {
        lock (_sync)
        {
            if (_attempts.Count == 0) return Array.Empty<ParryConfirmationScan>();
        }
        if (frame == null || frame.Width <= 0 || frame.Height <= 0 || frame.Stride == 0 || frame.Buffer == null)
            return Array.Empty<ParryConfirmationScan>();

        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            screenBounds = new Rectangle(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        Rectangle normalized = NormalizedRegion(screenBounds.Width, screenBounds.Height);
        Rectangle region = new(screenBounds.Left + normalized.Left, screenBounds.Top + normalized.Top,
            normalized.Width, normalized.Height);
        int brightPixels = CountBrightPixels(frame, region);
        var results = new List<ParryConfirmationScan>();
        lock (_sync)
        {
            for (int i = _attempts.Count - 1; i >= 0; i--)
            {
                Attempt attempt = _attempts[i];
                long elapsedMs = nowTick - attempt.SentTick;
                if (elapsedMs < 0) continue;

                if (elapsedMs > ConfirmationEndMs)
                {
                    results.Add(attempt.CreateScan(elapsedMs, brightPixels, region,
                        attempt.Baseline >= 0, false, false, ParryConfirmationResult.Unconfirmed));
                    _attempts.RemoveAt(i);
                    continue;
                }

                bool baselineEstablishedNow = false;
                bool qualifying = false;
                if (attempt.BaselineSamples.Count < 2)
                {
                    attempt.BaselineSamples.Add(brightPixels);
                    if (attempt.BaselineSamples.Count == 2)
                    {
                        // Use the higher of the first two scans so a transient
                        // pre-impact glow cannot lower the confirmation bar.
                        attempt.Baseline = Math.Max(attempt.BaselineSamples[0], attempt.BaselineSamples[1]);
                        attempt.Threshold = ScaledThreshold(attempt.Baseline, screenBounds.Width, screenBounds.Height);
                        baselineEstablishedNow = true;
                    }
                }
                else if (elapsedMs >= ConfirmationStartMs)
                {
                    qualifying = brightPixels >= attempt.Threshold;
                    attempt.ConsecutiveQualifying = qualifying ? attempt.ConsecutiveQualifying + 1 : 0;
                }

                ParryConfirmationResult result = attempt.ConsecutiveQualifying >= 2
                    ? ParryConfirmationResult.Confirmed
                    : ParryConfirmationResult.None;
                results.Add(attempt.CreateScan(elapsedMs, brightPixels, region,
                    attempt.Baseline >= 0, baselineEstablishedNow, qualifying, result));
                if (result == ParryConfirmationResult.Confirmed) _attempts.RemoveAt(i);
            }
        }
        results.Reverse();
        return results;
    }

    public void Clear()
    {
        lock (_sync) _attempts.Clear();
    }

    internal static Rectangle NormalizedRegion(int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(width * 0.12), 0, Math.Max(0, width - 1));
        int top = Math.Clamp((int)Math.Floor(height * 0.20), 0, Math.Max(0, height - 1));
        int right = Math.Clamp((int)Math.Ceiling(width * 0.72), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(height * 0.92), top + 1, height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    internal static int ScaledThreshold(int baseline, int width, int height)
    {
        int baseThreshold = Math.Max(MinimumBaseThreshold, baseline + BaselineDelta);
        double scale = (double)width * height / (ReferenceWidth * (double)ReferenceHeight);
        return Math.Max(1, (int)Math.Ceiling(baseThreshold * scale));
    }

    internal static int CountBrightPixels(ScreenFrame frame, Rectangle region)
    {
        Rectangle frameBounds = new(frame.OriginX, frame.OriginY, frame.Width, frame.Height);
        Rectangle clipped = Rectangle.Intersect(region, frameBounds);
        if (clipped.Width <= 0 || clipped.Height <= 0) return 0;
        int count = 0;
        for (int y = clipped.Top - frame.OriginY; y < clipped.Bottom - frame.OriginY; y++)
        {
            int row = y * frame.Stride;
            for (int x = clipped.Left - frame.OriginX; x < clipped.Right - frame.OriginX; x++)
            {
                int index = row + x * 4;
                int blue = frame.Buffer[index];
                int green = frame.Buffer[index + 1];
                int red = frame.Buffer[index + 2];
                if (red < 230 || green < 170 || blue < 70) continue;
                int luminance = (2126 * red + 7152 * green + 722 * blue) / 10000;
                if (luminance >= 210) count++;
            }
        }
        return count;
    }

    private sealed class Attempt
    {
        public Attempt(string attemptId, long candidateId, CombatDirection direction, long sentTick)
        {
            AttemptId = attemptId;
            CandidateId = candidateId;
            Direction = direction;
            SentTick = sentTick;
        }

        public string AttemptId { get; }
        public long CandidateId { get; }
        public CombatDirection Direction { get; }
        public long SentTick { get; }
        public List<int> BaselineSamples { get; } = new();
        public int Baseline { get; set; } = -1;
        public int Threshold { get; set; } = -1;
        public int ConsecutiveQualifying { get; set; }

        public ParryConfirmationScan CreateScan(long elapsedMs, int brightPixels, Rectangle region,
            bool baselineEstablished, bool baselineEstablishedNow,
            bool qualifying = false, ParryConfirmationResult result = ParryConfirmationResult.None) =>
            new(AttemptId, CandidateId, Direction, elapsedMs, brightPixels, Baseline, Threshold,
                baselineEstablished, baselineEstablishedNow, qualifying, ConsecutiveQualifying, region, result);
    }
}
