namespace HappyBot.App;

/// <summary>
/// The Test input button: each run picks the next output in the cycle, waits so
/// the user can switch to the game, then presses it five times.
/// </summary>
internal sealed class InputTestRunner : IDisposable
{
    public static readonly (string Name, Action Press)[] DefaultTests =
    {
        ("Dodge (A)", () => Input.KeyTap(Input.VK_SPACE)),
        ("Heavy (RT)", () => Input.MouseClick(Input.VK_RBUTTON)),
        ("Light (RB)", () => Input.MouseClick(Input.VK_LBUTTON)),
        ("Guard Break (X)", () => Input.KeyTap(Input.VK_NUMPAD5)),
        ("Guard Top", () => Input.KeyTap(Input.VK_NUMPAD8)),
        ("Guard Left", () => Input.KeyTap(Input.VK_NUMPAD4)),
        ("Guard Right", () => Input.KeyTap(Input.VK_NUMPAD6))
    };

    public static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PressInterval = TimeSpan.FromMilliseconds(250);
    public const int Presses = 5;

    private readonly IReadOnlyList<(string Name, Action Press)> _tests;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource _cts = new();
    private int _next;

    public InputTestRunner() : this(DefaultTests, Task.Delay)
    {
    }

    public InputTestRunner(IReadOnlyList<(string Name, Action Press)> tests, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _tests = tests ?? throw new ArgumentNullException(nameof(tests));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    /// <summary>
    /// Cancels any running test and starts the next one in the background.
    /// Returns the test name; <paramref name="completed"/> runs on a pool thread.
    /// </summary>
    public string StartNext(Action<string> completed)
    {
        (string name, Action press) = _tests[_next];
        _next = (_next + 1) % _tests.Count;
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _delay(LeadIn, token).ConfigureAwait(false);
                for (int i = 0; i < Presses && !token.IsCancellationRequested; i++)
                {
                    press();
                    await _delay(PressInterval, token).ConfigureAwait(false);
                }
                completed?.Invoke(name);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
        return name;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
