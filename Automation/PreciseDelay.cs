using System.Diagnostics;

namespace HappyBot.Automation;

/// <summary>
/// Task.Delay rides the ~15.6ms Windows timer tick, so a 55ms wait lands
/// anywhere between 55 and ~70ms. Side deflects only have a 67ms window, so
/// reaction delays sleep coarsely and spin the last stretch on a Stopwatch.
/// </summary>
internal static class PreciseDelay
{
    // Larger than one timer tick so the coarse sleep can never overshoot the target.
    private const int SpinMarginMs = 20;

    public static long Now => Stopwatch.GetTimestamp();

    public static double ElapsedMs(long startTimestamp, long endTimestamp) =>
        (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Waits until <paramref name="delayMs"/> after <paramref name="startTimestamp"/>.
    /// Completes synchronously when that moment has already passed.
    /// </summary>
    public static async Task WaitUntilAsync(long startTimestamp, int delayMs, CancellationToken token)
    {
        long target = startTimestamp + delayMs * Stopwatch.Frequency / 1000;
        long remainingMs = (target - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
        if (remainingMs > SpinMarginMs)
            await Task.Delay((int)(remainingMs - SpinMarginMs), token).ConfigureAwait(false);
        while (Stopwatch.GetTimestamp() < target)
        {
            token.ThrowIfCancellationRequested();
            Thread.SpinWait(64);
        }
        token.ThrowIfCancellationRequested();
    }
}
