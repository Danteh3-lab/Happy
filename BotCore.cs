namespace HappyBot;

public sealed class BotCore
{
    private const int LegitChancePercent = 55;
    private const int LegitCooldownMs = 1200;
    private long _lastReactionTick;
    public readonly Settings S = new();

    public double B55, Y55;
    public double X2, Y2, X3, Y3, X4, Y4, X5, Y5, X6, Y6, X7, Y7;
    public double X8, Y8, X9, Y9, X16, Y16, X17, Y17, X18, Y18, X19, Y19;

    public volatile bool MarkerFound;
    public volatile bool AttackIndicator;
    public volatile bool EHeld;
    public volatile bool FHeld;
    public volatile int ParryCount;
    public volatile string GuardDir = "-";
    public volatile bool Flash;
    public volatile int LoopHz;
    public volatile string LastError = "";
    public volatile int ScreenWidth;
    public volatile int ScreenHeight;
    public int Ax;
    public int Ay;
    public int Box;

    private readonly ManualResetEventSlim _paused = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Timer _releaseTimer;
    private readonly System.Threading.Timer _releTimer;
    private ScreenFrame _frame = new();
    private Thread _thread;

    public BotCore()
    {
        _releaseTimer = new System.Threading.Timer(_ => S.Ubp = 0, null, Timeout.Infinite, Timeout.Infinite);
        _releTimer = new System.Threading.Timer(_ => S.Active1 = 1, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsRunning => _thread is { IsAlive: true };

    public void Start()
    {
        if (IsRunning) return;
        _paused.Set();
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        _thread?.Join(2000);
    }

    public void TogglePause()
    {
        if (_paused.IsSet) _paused.Reset();
        else _paused.Set();
    }

    public void ScheduleRele() => _releTimer.Change(9000, Timeout.Infinite);

    private void Loop()
    {
        try
        {
            var sw = new System.Diagnostics.Stopwatch();
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _paused.Wait(_cts.Token);
                    sw.Restart();
                    Flash = false;
                    _frame = ScreenCapture.Capture(_frame);
                    ScreenWidth = _frame.Width;
                    ScreenHeight = _frame.Height;
                    Calculate();
                    SearchBot();
                    AutoBlock();
                    LoopHz = (int)(1000.0 / Math.Max(1.0, sw.Elapsed.TotalMilliseconds));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    Thread.Sleep(250);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool Px(double x1, double y1, double x2, double y2, int r, int g, int b, int variation, out int px, out int py)
    {
        return _frame.PixelSearch(x1, y1, x2, y2, r, g, b, variation, out px, out py);
    }

    private bool CurrentPx(double x1, double y1, double x2, double y2, int r, int g, int b, int variation, out int px, out int py)
    {
        return _frame.PixelSearch(x1, y1, x2, y2, r, g, b, variation, out px, out py);
    }

    private bool FreshIndicatorPx(int r, int g, int b, int variation, out int px, out int py)
    {
        px = py = 0;
        int left = (int)Math.Floor(Math.Min(X16, X17));
        int top = (int)Math.Floor(Math.Min(Y16, Y17));
        int right = (int)Math.Ceiling(Math.Max(X16, X17));
        int bottom = (int)Math.Ceiling(Math.Max(Y16, Y17));
        var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        left = Math.Clamp(left, bounds.Left, bounds.Right - 1);
        top = Math.Clamp(top, bounds.Top, bounds.Bottom - 1);
        right = Math.Clamp(right, left + 1, bounds.Right);
        bottom = Math.Clamp(bottom, top + 1, bounds.Bottom);

        _frame = ScreenCapture.Capture(_frame, new Rectangle(left, top, right - left, bottom - top));
        ScreenWidth = bounds.Width;
        ScreenHeight = bounds.Height;
        if (!_frame.PixelSearch(0, 0, _frame.Width - 1, _frame.Height - 1, r, g, b, variation, out int localX, out int localY))
            return false;

        px = localX + left;
        py = localY + top;
        return true;
    }

    public string DebugScan()
    {
        var f = ScreenCapture.Capture(null);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Screen captured: {f.Width}x{f.Height}");
        sb.AppendLine($"Search region: ({X8:0},{Y8:0})-({X9:0},{Y9:0})");
        sb.AppendLine($"Scalers: B55={B55:0.###} Y55={Y55:0.###}");
        sb.AppendLine();

        int black = 0;
        for (int y = 0; y < f.Height; y += 97)
        {
            for (int x = 0; x < f.Width; x += 97)
            {
                if (f.SamplePixel(x, y, out int r, out int g, out int b) && r + g + b < 15)
                    black++;
            }
        }
        sb.AppendLine($"Frame is {(black > 300 ? "BLACK (capture likely blocked)" : "OK (not black)")}");
        sb.AppendLine();

        int sx = Math.Max(0, Math.Min((int)X8, f.Width - 1));
        int ex = Math.Max(0, Math.Min((int)X9, f.Width - 1));
        int sy = Math.Max(0, Math.Min((int)Y8, f.Height - 1));
        int ey = Math.Max(0, Math.Min((int)Y9, f.Height - 1));

        int fx, fy;
        int inRegionGreen = 0, inRegionYellow = 0;
        for (int y = sy; y <= ey && inRegionGreen < 50; y++)
        {
            for (int x = sx; x <= ex && inRegionGreen < 50; x++)
            {
                if (f.SamplePixel(x, y, out int r, out int g, out int b))
                {
                    if (Math.Abs(r - 5) <= 10 && Math.Abs(g - 131) <= 10 && Math.Abs(b - 65) <= 10)
                    {
                        if (inRegionGreen == 0) { fx = x; fy = y; sb.AppendLine($"GREEN inside region first at ({fx},{fy})"); }
                        inRegionGreen++;
                    }
                    if (Math.Abs(r - 255) <= 10 && Math.Abs(g - 255) <= 10 && Math.Abs(b - 10) <= 10)
                        inRegionYellow++;
                }
            }
        }
        sb.AppendLine($"Green inside region: {inRegionGreen} px | Yellow inside region: {inRegionYellow} px");
        sb.AppendLine();

        int greens = f.CountColor(5, 131, 65, 10, out fx, out fy);
        sb.AppendLine($"Green anywhere (var10): {greens}" + (greens > 0 ? $" first at ({fx},{fy})" : ""));
        int yellows = f.CountColor(255, 255, 10, 10, out fx, out fy);
        sb.AppendLine($"Yellow anywhere (var10): {yellows}" + (yellows > 0 ? $" first at ({fx},{fy})" : ""));
        sb.AppendLine();

        int cx = (int)((X8 + X9) / 2);
        sb.AppendLine("Vertical strip at region center X=" + cx + ":");
        for (int y = sy; y <= ey; y += Math.Max(1, (ey - sy) / 10))
            sb.AppendLine($"  y={y}: {Sample(f, cx, y)}");
        sb.AppendLine($"Screen center: {Sample(f, f.Width / 2, f.Height / 2)}");
        return sb.ToString();
    }

    private static string Sample(ScreenFrame f, int x, int y)
    {
        return f.SamplePixel(x, y, out int r, out int g, out int b) ? $"RGB({r},{g},{b})" : "off-screen";
    }

    private static void Sleep(int ms) => Thread.Sleep(ms);

    private static void WithBlock(Action action)
    {
        Input.Block(true);
        try
        {
            action();
        }
        finally
        {
            Input.Block(false);
        }
    }

    private void SetCoords(double nx2, double ny2, double nx3, double ny3, double nx4, double ny4,
                          double nx5, double ny5, double nx6, double ny6, double nx7, double ny7,
                          double nx16, double ny16, double nx17, double ny17)
    {
        X2 = nx2; Y2 = ny2;
        X3 = nx3; Y3 = ny3;
        X4 = nx4; Y4 = ny4;
        X5 = nx5; Y5 = ny5;
        X6 = nx6; Y6 = ny6;
        X7 = nx7; Y7 = ny7;
        X16 = nx16; Y16 = ny16;
        X17 = nx17; Y17 = ny17;
    }

    private void Calculate()
    {
        _frame = ScreenCapture.Capture(_frame);
        ScreenWidth = _frame.Width;
        ScreenHeight = _frame.Height;

        if (CurrentPx(X18, Y18, X19, Y19, 5, 131, 65, 10, out _, out _)) Box = 1;
        else Box = 2;

        if (CurrentPx(X8, Y8, X9, Y9, 5, 131, 65, 10, out int ax, out int ay))
        {
            Ax = ax;
            Ay = ay;
            MarkerFound = true;
            if (Box == 2)
                SetCoords(ax - 200 * B55, ay + 20 * Y55, ax + 160 * B55, ay + 170 * Y55,
                          ax + 5 * B55, ay + 195 * Y55, ax + 160 * B55, ay + 430 * Y55,
                          ax - 200 * B55, ay + 195 * Y55, ax - 30 * B55, ay + 430 * Y55,
                          ax - 200 * B55, ay + 20 * Y55, ax + 160 * B55, ay + 430 * Y55);
            else
                SetCoords(ax - 100 * B55, ay + 10 * B55, ax + 80 * Y55, ay + 85 * Y55,
                          ax + 2.5 * B55, ay + 97.5 * Y55, ax + 80 * B55, ay + 227.7 * Y55,
                          ax - 100 * B55, ay + 97.5 * Y55, ax - 15 * B55, ay + 227.7 * Y55,
                          ax - 117.6 * B55, ay + 10 * Y55, ax + 94.11 * B55, ay + 227.7 * Y55);
        }
        else if (CurrentPx(X8, Y8, X9, Y9, 255, 255, 10, 10, out ax, out ay))
        {
            Ax = ax;
            Ay = ay;
            MarkerFound = true;
            if (Box == 2)
                SetCoords(ax - 175 * B55, ay + 65 * Y55, ax + 185 * B55, ay + 185 * Y55,
                          ax + 30 * B55, ay + 215 * Y55, ax + 185 * B55, ay + 430 * Y55,
                          ax - 175 * B55, ay + 215 * Y55, ax - 5 * B55, ay + 430 * Y55,
                          ax - 175 * B55, ay + 65 * Y55, ax + 185 * B55, ay + 430 * Y55);
            else
                SetCoords(ax - 87.5 * B55, ay + 35 * B55, ax + 92.5 * Y55, ay + 92.5 * Y55,
                          ax + 15 * B55, ay + 107.5 * Y55, ax + 92.5 * B55, ay + 215 * Y55,
                          ax - 87.5 * B55, ay + 107.5 * Y55, ax - 2.5 * B55, ay + 215 * Y55,
                          ax - 87.5 * B55, ay + 35 * Y55, ax + 92.5 * B55, ay + 215 * Y55);
        }
        else
        {
            MarkerFound = false;
        }
    }

    private void SearchBot()
    {
        if (!S.Unblockables) return;
        if (!CurrentPx(X16, Y16, X17, Y17, 246, 98, 8, 10, out _, out _)) return;

        if (CurrentPx(X16, Y16, X17, Y17, 255, 34, 28, 10, out _, out _))
        {
            S.Ubp = 1;
            _releaseTimer.Change(1000, Timeout.Infinite);
            UbParry();
            return;
        }

        if (S.Active1 == 0) return;

        if (S.Nohero)
        {
            Dodge2();
            return;
        }

        if (S.Ch("Blackprior")) Dodge1();
        else Dodge2();
        if (S.Ch("Nobushi")) Dodge4();
        if (S.Ch("Shaman")) Dodge5();
        if (S.Ch("Orochi")) Dodge6();
        if (S.Ch("Jiangjun")) Dodge7();
    }

    private void AutoBlock()
    {
        GuardDir = "-";
        AttackIndicator = false;
        if (!S.Autoblock) return;
        if (!FreshIndicatorPx(255, 49, 41, 15, out int zx, out int zy)) return;
        AttackIndicator = true;

        if (zx > X4 && zy > Y4) { RGT(); return; }
        if (zx < X7 && zy > Y4) { LFT(); return; }
        if (zy > Y2 && zy < Y3) { TOP(); return; }
        GuardDir = "UNKNOWN";
    }

    private void UbParry()
    {
        if (!S.Unblockables)
        {
            S.Ubp = 0;
            return;
        }

        while (S.Ubp == 1)
        {
            _frame = ScreenCapture.Capture(_frame);
            ScreenWidth = _frame.Width;
            ScreenHeight = _frame.Height;
            if (CurrentPx(X16, Y16, X17, Y17, 255, 34, 28, 10, out _, out _)) continue;

            Sleep(S.Pause1);
            _frame = ScreenCapture.Capture(_frame);
            ScreenWidth = _frame.Width;
            ScreenHeight = _frame.Height;
            if (!CurrentPx(X16, Y16, X17, Y17, 246, 98, 8, 10, out _, out _) || !S.Unblockables) continue;

            if (Input.IsDown(Input.VK_W))
            {
                if (S.Ch("Blackprior"))
                {
                    Input.KeyTap(Input.VK_NUMPAD9);
                    Sleep(2300);
                    return;
                }
                Input.Block(true);
                try
                {
                    Input.KeyDown(Input.VK_DOWN);
                    Input.KeyTap(Input.VK_SPACE);
                    Input.KeyUp(Input.VK_DOWN);
                }
                finally
                {
                    Input.Block(false);
                }
                Sleep(700);
                return;
            }

            if (S.Pbp == 0) Input.MouseClick(Input.VK_RBUTTON);
            else
            {
                Input.KeyDown(Input.VK_DOWN);
                Input.KeyTap(Input.VK_SPACE);
                Input.KeyUp(Input.VK_DOWN);
            }
            Sleep(700);
        }
    }

    private void Dodge1()
    {
        if (!S.Unblockables) return;

        if (S.Unblockables)
        {
            Sleep(S.Pause);
            if (Input.IsDown(Input.VK_W))
            {
                Input.KeyTap(Input.VK_NUMPAD9);
                Sleep(2000);
                return;
            }
            Input.KeyTap(Input.VK_SPACE);
        }

        if (S.DodgeL)
        {
            Sleep(S.Pause2);
            Input.MouseClick(Input.VK_LBUTTON);
        }

        Sleep(500);
    }

    private void Dodge2()
    {
        if (!S.Unblockables || (!S.Nohero &&
            (S.Ch("Nobushi") || S.Ch("Shaman") || S.Ch("Orochi") || S.Ch("Jiangjun")))) return;

        Sleep(S.Pause);
        if (Input.IsDown(Input.VK_W))
        {
            WithBlock(() =>
            {
                Input.KeyDown(Input.VK_DOWN);
                Input.KeyTap(Input.VK_SPACE);
                Input.KeyUp(Input.VK_DOWN);
            });
            Sleep(700);
            return;
        }

        Input.KeyTap(Input.VK_SPACE);

        if (S.DodgeL)
        {
            Sleep(S.Pause2);
            Input.MouseClick(Input.VK_LBUTTON);
        }

        if (S.DodgeH)
        {
            Sleep(S.Pause2);
            Input.MouseClick(Input.VK_RBUTTON);
        }

        if (S.Lightbash)
        {
            Sleep(S.Pause2);
            Input.KeyTap(Input.VK_NUMPAD5);
            Sleep(700);
        }

        Sleep(700);
    }

    private void Dodge4()
    {
        if (!S.Unblockables || S.Nohero || !S.Ch("Nobushi")) return;
        Sleep(S.Pause2);
        Input.KeyTap(Input.VK_C);
    }

    private void Dodge5()
    {
        if (!S.Unblockables || S.Nohero || !S.Ch("Shaman")) return;
        Sleep(S.Pause2);
        Input.KeyDown(Input.VK_SPACE);
        Input.KeyTap(Input.VK_NUMPAD5);
        Input.KeyUp(Input.VK_SPACE);
        Sleep(2000);
    }

    private void Dodge6()
    {
        if (!S.Unblockables || S.Nohero || !S.Ch("Orochi")) return;
        Sleep(S.Pause2);
        Input.KeyTap(Input.VK_SPACE);
        Input.KeyTap(Input.VK_NUMPAD9);
        Sleep(500);
    }

    private void Dodge7()
    {
        if (!S.Unblockables || S.Nohero || !S.Ch("Jiangjun")) return;
        Sleep(S.Pause2);
        Input.KeyDown(Input.VK_C);
        Sleep(250);
        Input.MouseClick(Input.VK_LBUTTON);
        Input.MouseClick(Input.VK_RBUTTON);
        Input.KeyUp(Input.VK_C);
        Sleep(2000);
    }

    private bool AttackFlashing()
    {
        double left = Math.Min(X16, X17), top = Math.Min(Y16, Y17);
        double right = Math.Max(X16, X17), bottom = Math.Max(Y16, Y17);
        int w = (int)(right - left), h = (int)(bottom - top);
        if (w > 0 && h > 0)
            _frame = ScreenCapture.Capture(_frame, new Rectangle((int)left, (int)top, w, h));
        else
            _frame = ScreenCapture.Capture(_frame);
        ScreenWidth = _frame.Width;
        ScreenHeight = _frame.Height;
        if (_frame.PixelSearch(0, 0, _frame.Width - 1, _frame.Height - 1, 255, 41, 34, 0, out _, out _))
        {
            Flash = false;
            return false;
        }

        bool lightFlash = _frame.PixelSearch(0, 0, _frame.Width - 1, _frame.Height - 1, 255, 154, 141, 0, out _, out _);
        Flash = lightFlash;
        return Flash;
    }

    private bool YourChar(string name) => S.YourHero && !S.Nohero && S.Ch(name);

    private bool HasEAction() => S.Parry2 || S.Crushing2;

    private bool ReactionAllowed()
    {
        if (!S.Legit) return true;
        long now = Environment.TickCount64;
        if (now - _lastReactionTick < LegitCooldownMs) return false;
        if (Random.Shared.Next(100) >= LegitChancePercent) return false;
        _lastReactionTick = now;
        return true;
    }

    private bool HasFAction() => S.Parry || S.Crushing || S.Deflect || HasHeroAction();

    private bool HasHeroAction() =>
        S.YourHero && !S.Nohero &&
        (S.Ch("Warden") || S.Ch("Blackprior") || S.Ch("Warlord") ||
         S.Ch("Shaman") || S.Ch("Varangian") || S.Ch("Orochi") ||
         S.Ch("Nobushi") || S.Ch("Aramusha") || S.Ch("Jiangjun"));

    private void SendParry()
    {
        Input.MouseClick(Input.VK_RBUTTON);
        ParryCount++;
    }

    private void DeflectBack()
    {
        WithBlock(() =>
        {
            Input.KeyUp(Input.VK_W);
            Input.KeyUp(Input.VK_S);
            Input.KeyUp(Input.VK_A);
            Input.KeyUp(Input.VK_D);
            Input.KeyDown(Input.VK_UP);
            Input.KeyTap(Input.VK_SPACE);
            Input.KeyUp(Input.VK_UP);
        });
        Sleep(700);
    }

    private void DeflectLeft()
    {
        WithBlock(() =>
        {
            Input.KeyUp(Input.VK_W);
            Input.KeyUp(Input.VK_S);
            Input.KeyUp(Input.VK_A);
            Input.KeyUp(Input.VK_D);
            Sleep(S.Left);
            Input.KeyDown(Input.VK_LEFT);
            Input.KeyTap(Input.VK_SPACE);
            Input.KeyUp(Input.VK_LEFT);
        });
        Sleep(700);
    }

    private void DeflectRight()
    {
        WithBlock(() =>
        {
            Input.KeyUp(Input.VK_W);
            Input.KeyUp(Input.VK_S);
            Input.KeyUp(Input.VK_A);
            Input.KeyUp(Input.VK_D);
            Sleep(S.Right);
            Input.KeyDown(Input.VK_RIGHT);
            Input.KeyTap(Input.VK_SPACE);
            Input.KeyUp(Input.VK_RIGHT);
        });
        Sleep(700);
    }

    private void TOP()
    {
        GuardDir = "TOP";
        Sleep(S.Pause3);
        Input.KeyTap(Input.VK_NUMPAD8, 60);

        if (EHeld && HasEAction())
        {
            while (EHeld)
            {
                if (!AttackFlashing()) continue;
                if (!ReactionAllowed()) { while (AttackFlashing()) { } continue; }

                if (S.Parry2) { SendParry(); Sleep(850); return; }
                if (S.Crushing2) { Input.MouseClick(Input.VK_LBUTTON); Sleep(1200); return; }
            }
        }

        if (FHeld && HasFAction())
        {
            while (FHeld)
            {
                if (!AttackFlashing()) continue;
                if (!ReactionAllowed()) { while (AttackFlashing()) { } continue; }

                if (YourChar("Warden"))
                {
                    Input.MouseClick(Input.VK_LBUTTON);
                    Sleep(500);
                    Input.MouseClick(Input.VK_LBUTTON);
                    Sleep(850);
                    return;
                }
                if (YourChar("Blackprior")) { Input.KeyTap(Input.VK_NUMPAD9); Sleep(2000); return; }
                if (YourChar("Warlord")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_LBUTTON); Sleep(250); return; }
                if (YourChar("Shaman"))
                {
                    Input.KeyTap(Input.VK_SPACE);
                    Input.KeyTap(Input.VK_NUMPAD5);
                    Sleep(2000);
                    return;
                }
                if (YourChar("Varangian")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_RBUTTON); Sleep(2000); return; }
                if (YourChar("Orochi"))
                {
                    Input.KeyTap(Input.VK_SPACE);
                    Input.KeyTap(Input.VK_NUMPAD9);
                    Sleep(500);
                    return;
                }
                if (YourChar("Nobushi")) { Input.KeyTap(Input.VK_C); Sleep(250); return; }
                if (YourChar("Aramusha")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_RBUTTON); Sleep(850); return; }
                if (YourChar("Jiangjun"))
                {
                    Input.KeyDown(Input.VK_C);
                    Sleep(250);
                    Input.MouseClick(Input.VK_LBUTTON);
                    Input.MouseClick(Input.VK_RBUTTON);
                    Input.KeyUp(Input.VK_C);
                    Sleep(250);
                    return;
                }
                if (S.Parry) { SendParry(); Sleep(850); return; }
                if (S.Crushing) { Input.MouseClick(Input.VK_LBUTTON); Sleep(1200); return; }
                if (S.Deflect) { DeflectBack(); return; }
            }
        }
    }

    private void LFT()
    {
        GuardDir = "LFT";
        Sleep(S.Pause3);
        Input.KeyTap(Input.VK_NUMPAD4, 60);

        if (EHeld && HasEAction())
        {
            while (EHeld)
            {
                if (!AttackFlashing()) continue;
                if (!ReactionAllowed()) { while (AttackFlashing()) { } continue; }

                if (S.Parry2) { SendParry(); Sleep(850); return; }
                if (S.Crushing2) { Input.MouseClick(Input.VK_LBUTTON); Sleep(1200); return; }
            }
        }

        if (FHeld && HasFAction())
        {
            while (FHeld)
            {
                if (!AttackFlashing()) continue;
                if (!ReactionAllowed()) { while (AttackFlashing()) { } continue; }

                if (S.Parry) { SendParry(); Sleep(850); return; }
                if (YourChar("Blackprior")) { Input.KeyTap(Input.VK_NUMPAD9); Sleep(2000); return; }
                if (YourChar("Warlord")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_LBUTTON); Sleep(250); return; }
                if (YourChar("Shaman"))
                {
                    Input.KeyTap(Input.VK_SPACE);
                    Input.KeyTap(Input.VK_NUMPAD5);
                    Sleep(2000);
                    return;
                }
                if (YourChar("Varangian")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_RBUTTON); Sleep(2000); return; }
                if (YourChar("Orochi"))
                {
                    Input.KeyTap(Input.VK_SPACE);
                    Input.KeyTap(Input.VK_NUMPAD9);
                    Sleep(500);
                    return;
                }
                if (YourChar("Nobushi")) { Input.KeyTap(Input.VK_C); Sleep(250); return; }
                if (YourChar("Aramusha")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_RBUTTON); Sleep(850); return; }
                if (YourChar("Jiangjun"))
                {
                    Input.KeyDown(Input.VK_C);
                    Sleep(250);
                    Input.MouseClick(Input.VK_LBUTTON);
                    Input.MouseClick(Input.VK_RBUTTON);
                    Input.KeyUp(Input.VK_C);
                    Sleep(250);
                    return;
                }
                if (S.Crushing) { Input.MouseClick(Input.VK_LBUTTON); Sleep(1200); return; }
                if (S.Deflect) { DeflectLeft(); return; }
            }
        }
    }

    private void RGT()
    {
        GuardDir = "RGT";
        Sleep(S.Pause3);
        Input.KeyTap(Input.VK_NUMPAD6, 60);

        if (EHeld && HasEAction())
        {
            while (EHeld)
            {
                if (!AttackFlashing()) continue;
                if (!ReactionAllowed()) { while (AttackFlashing()) { } continue; }

                if (S.Parry2) { SendParry(); Sleep(850); return; }
                if (S.Crushing2) { Input.MouseClick(Input.VK_LBUTTON); Sleep(1200); return; }
            }
        }

        if (FHeld && HasFAction())
        {
            while (FHeld)
            {
                if (!AttackFlashing()) continue;
                if (!ReactionAllowed()) { while (AttackFlashing()) { } continue; }

                if (S.Parry) { SendParry(); Sleep(850); return; }
                if (YourChar("Blackprior")) { Input.KeyTap(Input.VK_NUMPAD9); Sleep(2000); return; }
                if (YourChar("Warlord")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_LBUTTON); Sleep(250); return; }
                if (YourChar("Shaman"))
                {
                    Input.KeyTap(Input.VK_SPACE);
                    Input.KeyTap(Input.VK_NUMPAD5);
                    Sleep(2000);
                    return;
                }
                if (YourChar("Varangian")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_RBUTTON); Sleep(2000); return; }
                if (YourChar("Orochi"))
                {
                    Input.KeyTap(Input.VK_SPACE);
                    Input.KeyTap(Input.VK_NUMPAD9);
                    Sleep(500);
                    return;
                }
                if (YourChar("Nobushi")) { Input.KeyTap(Input.VK_C); Sleep(250); return; }
                if (YourChar("Aramusha")) { Input.KeyTap(Input.VK_C); Input.MouseClick(Input.VK_RBUTTON); Sleep(850); return; }
                if (YourChar("Jiangjun"))
                {
                    Input.KeyDown(Input.VK_C);
                    Sleep(250);
                    Input.MouseClick(Input.VK_LBUTTON);
                    Input.MouseClick(Input.VK_RBUTTON);
                    Input.KeyUp(Input.VK_C);
                    Sleep(250);
                    return;
                }
                if (S.Crushing) { Input.MouseClick(Input.VK_LBUTTON); Sleep(2000); return; }
                if (S.Deflect) { DeflectRight(); return; }
            }
        }
    }
}
