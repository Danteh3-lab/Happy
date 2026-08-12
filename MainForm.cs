using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HappyBot;

public sealed class MainForm : Form
{
    private const int IdF1 = 1, IdF2 = 2, IdF3 = 3, IdF4 = 4, IdF5 = 5, IdF6 = 6, IdF7 = 7;
    private const int PeacekeeperDeflectDelayMs = 100;
    private const int MaxDelayMs = 10000;
    private int _prevLeftDeflect;
    private int _prevRightDeflect;
    private bool _peacekeeperApplied;
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    private static readonly string[] EditKeys = { "res1", "res2", "Pause", "Pause1", "Pause2", "Pause3", "ParryDelay", "GuardHold", "Left", "Right" };

    private static readonly string[] CheckKeys =
    {
        "DodgeL", "DodgeH", "Leftdodge", "Rightdodge", "Unblockables", "Autoblock", "Lightbash",
        "Parry", "Crushing", "Deflect", "Parry2", "Crushing2", "Nohero", "YourHero", "Legit",
        "Warden", "Peacekeeper", "Centurion", "Blackprior", "Gryphon", "Conqueror", "Lawbringer", "Gladiator", "Warmonger",
        "Raider", "Berserker", "Highlander", "Jormungandr", "Warlord", "Valkyrie", "Shaman", "Varangian", "Null",
        "Kensei", "Orochi", "Shinobi", "Hitokiri", "Sohei", "Shugoki", "Nobushi", "Aramusha", "Kyoshin",
        "Tiandi", "Nuxia", "Zhanhu", "Jiangjun", "Shaolin", "Juren",
        "Pirate", "Afeera", "Medjay", "Khatun", "Ocelotl", "Virtuosa"
    };

    private static readonly string[] HeroKeys = new Settings().Chars.Keys.ToArray();

    private static readonly (string Name, Action Press)[] Tests =
    {
        ("Dodge (A)", () => Input.KeyTap(Input.VK_SPACE)),
        ("Heavy (RT)", () => Input.MouseClick(Input.VK_RBUTTON)),
        ("Light (RB)", () => Input.MouseClick(Input.VK_LBUTTON)),
        ("Guard Break (X)", () => Input.KeyTap(Input.VK_NUMPAD5)),
        ("Guard Top", () => Input.KeyTap(Input.VK_NUMPAD8)),
        ("Guard Left", () => Input.KeyTap(Input.VK_NUMPAD4)),
        ("Guard Right", () => Input.KeyTap(Input.VK_NUMPAD6))
    };

    private readonly BotCore _bot = new();
    private readonly KeyboardHook _hook;
    private readonly WebView2 _webView;
    private readonly VisionOverlayForm _visionOverlay;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private CancellationTokenSource _testCts = new();
    private int _testMode;
    private bool _shiftDown;
    private bool _rDown;
    private bool _fKeyDown;
    private bool _webReady;
    private bool _prevDpadDown;
    private bool _visionOverlayVisible;
    private bool _showAnchorScan = true;

    public MainForm()
    {
        Text = "DANBOT Control Deck";
        BackColor = Color.FromArgb(12, 12, 15);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 620);

        var screen = Screen.PrimaryScreen.Bounds;
        _bot.UpdateSettings(s =>
        {
            s.Res1 = screen.Width.ToString();
            s.Res2 = screen.Height.ToString();
        });
        ApplyResolution(screen.Width, screen.Height);

        _visionOverlay = new VisionOverlayForm(_bot.GetVisionSnapshot);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            AllowExternalDrop = false
        };
        Controls.Add(_webView);

        _hook = new KeyboardHook(OnKey);
        _statusTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _statusTimer.Tick += (_, _) =>
        {
            ViGEmInput.TryRecover();
            _bot.FHeld = _fKeyDown || Input.HoldButtonHeld();

            bool dpadDown = false;
            if (ViGEmInput.TryGetSourceState(out var sourceState))
                dpadDown = (sourceState.wButtons & 0x0002) != 0;
            if (dpadDown && !_prevDpadDown)
            {
                bool enabled = false;
                _bot.UpdateSettings(s => enabled = s.Parry = !s.Parry);
                _bot.ParryToggle = enabled;
                SendSettings();
                SendToast(enabled ? "Auto parry ON" : "Auto parry OFF", enabled ? "success" : "info");
            }
            _prevDpadDown = dpadDown;

            SendStatus();
        };
        _statusTimer.Start();
        Shown += (_, _) => _ = InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(AppContext.BaseDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _webReady = true;
                SendInit();
            };

            string page = Path.Combine(AppContext.BaseDirectory, "ui", "index.html");
            _webView.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 could not start. Install the Microsoft Edge WebView2 Runtime and try again.\n\n" + ex.Message, "DANBOT UI Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement)) return;

            switch (typeElement.GetString())
            {
                case "ready":
                    _webReady = true;
                    SendInit();
                    break;
                case "settings":
                    if (root.TryGetProperty("settings", out JsonElement settings))
                    {
                        ApplySettings(settings);
                        SendSettings();
                    }
                    break;
                case "start":
                    OnStart();
                    break;
                case "resolution":
                    OnResolution();
                    break;
                case "scan":
                    OnScan();
                    break;
                case "test":
                    OnTestInput();
                    break;
                case "save":
                    OnSave();
                    break;
                case "load":
                    OnLoad();
                    break;
                case "apply":
                    OnApply();
                    break;
                case "howto":
                    SendDialog("How to use", HowToText, "SETUP GUIDE");
                    break;
                case "readme":
                    SendDialog("Feature notes", ReadMeText, "REFERENCE");
                    break;
                case "reload":
                    Application.Restart();
                    break;
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "close":
                    Close();
                    break;
                case "drag":
                    DragWindow();
                    break;
                case "orange-parry":
                    ToggleOrangeParry();
                    break;
                case "vision-overlay":
                    ToggleVisionOverlay();
                    break;
                case "anchor-scan":
                    ToggleAnchorScan();
                    break;
                case "telemetry":
                    ToggleTelemetry(root.TryGetProperty("label", out JsonElement label) ? label.GetString() ?? "Other" : "Other");
                    break;
                case "export-telemetry":
                    ExportTelemetry();
                    break;
            }
        }
        catch (Exception ex)
        {
            SendToast("UI message failed: " + ex.Message, "error");
        }
    }

    private void SendInit()
    {
        SendToUi(new { type = "init", settings = SettingsSnapshot(), status = StatusSnapshot() });
    }

    private void SendStatus()
    {
        SendToUi(new { type = "status", status = StatusSnapshot() });
    }

    private object StatusSnapshot()
    {
        TelemetryStatus telemetry = _bot.Telemetry;
        return new
        {
            running = _bot.IsRunning,
            error = _bot.LastError,
            marker = _bot.MarkerFound ? "FOUND" : "MISSING",
            hold = _bot.FHeld ? "DOWN" : "UP",
            indicator = _bot.AttackIndicator ? "YES" : "NO",
            guard = _bot.GuardDir,
            flash = _bot.Flash ? "YES" : "NO",
            parryCount = _bot.ParryCount,
            injected = Input.InjectedCount,
            mode = Input.ActiveMode,
            source = ViGEmInput.SourceConnected ? "ON" : "OFF",
            sourceSlot = ViGEmInput.SourceSlot,
            virtualState = ViGEmInput.IsAvailable ? "ON" : "OFF",
            loop = _bot.LoopHz,
            dodgeEnabled = _bot.DodgeEnabled,
            legit = _bot.S.Legit,
            parryToggle = _bot.ParryToggle,
            orangeParry = _bot.OrangeParry,
            visionOverlay = _visionOverlayVisible,
            anchorScan = _showAnchorScan,
            telemetry = new
            {
                recording = telemetry.Recording,
                label = telemetry.Label,
                durationSeconds = (int)telemetry.Duration.TotalSeconds,
                failures = telemetry.Failures,
                dropped = telemetry.DroppedItems
            }
        };
    }

    private Dictionary<string, object> SettingsSnapshot()
    {
        var s = _bot.S;
        var values = new Dictionary<string, object>
        {
            ["res1"] = s.Res1,
            ["res2"] = s.Res2,
            ["Pause"] = s.Pause,
            ["Pause1"] = s.Pause1,
            ["Pause2"] = s.Pause2,
            ["Pause3"] = s.Pause3,
            ["ParryDelay"] = s.ParryDelay,
            ["GuardHold"] = s.GuardHold,
            ["Left"] = s.Left,
            ["Right"] = s.Right
        };
        foreach (string key in CheckKeys) values[key] = GetCheck(s, key);
        return values;
    }

    private static bool GetCheck(Settings s, string key)
    {
        return key switch
        {
            "DodgeL" => s.DodgeL,
            "DodgeH" => s.DodgeH,
            "Leftdodge" => s.Leftdodge,
            "Rightdodge" => s.Rightdodge,
            "Unblockables" => s.Unblockables,
            "Autoblock" => s.Autoblock,
            "Lightbash" => s.Lightbash,
            "Parry" => s.Parry,
            "Crushing" => s.Crushing,
            "Deflect" => s.Deflect,
            "Parry2" => s.Parry2,
            "Crushing2" => s.Crushing2,
            "Nohero" => s.Nohero,
            "YourHero" => s.YourHero,
            "Legit" => s.Legit,
            _ => s.Ch(key)
        };
    }

    private static void SetCheck(Settings s, string key, bool value)
    {
        switch (key)
        {
            case "DodgeL": s.DodgeL = value; break;
            case "DodgeH": s.DodgeH = value; break;
            case "Leftdodge": s.Leftdodge = value; if (value) s.Rightdodge = false; break;
            case "Rightdodge": s.Rightdodge = value; if (value) s.Leftdodge = false; break;
            case "Unblockables": s.Unblockables = value; break;
            case "Autoblock": s.Autoblock = value; break;
            case "Lightbash": s.Lightbash = value; break;
            case "Parry": s.Parry = value; break;
            case "Crushing": s.Crushing = value; break;
            case "Deflect": s.Deflect = value; break;
            case "Parry2": s.Parry2 = value; break;
            case "Crushing2": s.Crushing2 = value; break;
            case "Nohero": s.Nohero = value; break;
            case "YourHero": s.YourHero = value; break;
            case "Legit": s.Legit = value; break;
            default: s.Chars[key] = value; break;
        }
    }

    private void ApplySettings(JsonElement values)
    {
        bool parryChanged = values.TryGetProperty("Parry", out _);
        bool parryEnabled = _bot.S.Parry;
        _bot.UpdateSettings(s =>
        {
            s.Res1 = ReadString(values, "res1", s.Res1);
            s.Res2 = ReadString(values, "res2", s.Res2);
            s.Pause = ClampDelay(ReadInt(values, "Pause", s.Pause));
            s.Pause1 = ClampDelay(ReadInt(values, "Pause1", s.Pause1));
            s.Pause2 = ClampDelay(ReadInt(values, "Pause2", s.Pause2));
            s.Pause3 = ClampDelay(ReadInt(values, "Pause3", s.Pause3));
            s.ParryDelay = ClampDelay(ReadInt(values, "ParryDelay", s.ParryDelay));
            s.GuardHold = Math.Clamp(ReadInt(values, "GuardHold", s.GuardHold), 60, MaxDelayMs);
            s.Left = ClampDelay(ReadInt(values, "Left", s.Left));
            s.Right = ClampDelay(ReadInt(values, "Right", s.Right));
            foreach (string key in CheckKeys)
            {
                if (values.TryGetProperty(key, out _))
                    SetCheck(s, key, ReadBool(values, key, GetCheck(s, key)));
            }

            NormalizeHeroSelection(s);
            if (values.TryGetProperty("Peacekeeper", out _))
            {
                bool peacekeeperOn = s.Ch("Peacekeeper");
                if (peacekeeperOn && !_peacekeeperApplied)
                {
                    _prevLeftDeflect = s.Left;
                    _prevRightDeflect = s.Right;
                    s.Left = PeacekeeperDeflectDelayMs;
                    s.Right = PeacekeeperDeflectDelayMs;
                    _peacekeeperApplied = true;
                }
                else if (!peacekeeperOn && _peacekeeperApplied)
                {
                    s.Left = _prevLeftDeflect;
                    s.Right = _prevRightDeflect;
                    _peacekeeperApplied = false;
                }
            }
            parryEnabled = s.Parry;
        });
        if (parryChanged) _bot.ParryToggle = parryEnabled;
    }

    private static int ClampDelay(int value) => Math.Clamp(value, 0, MaxDelayMs);

    private static void NormalizeHeroSelection(Settings s)
    {
        string selected = HeroKeys.FirstOrDefault(s.Ch);
        if (selected == null) return;
        foreach (string hero in HeroKeys) s.Chars[hero] = hero.Equals(selected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadString(JsonElement values, string key, string fallback)
    {
        if (!values.TryGetProperty(key, out JsonElement value)) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
    }

    private static int ReadInt(JsonElement values, string key, int fallback)
    {
        if (!values.TryGetProperty(key, out JsonElement value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        return int.TryParse(value.ToString(), out number) ? number : fallback;
    }

    private static bool ReadBool(JsonElement values, string key, bool fallback)
    {
        if (!values.TryGetProperty(key, out JsonElement value)) return fallback;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return value.ToString() is "1" or "true" or "True";
    }

    private void OnResolution()
    {
        if (!TryReadResolution(out int width, out int height))
        {
            SendToast("Enter a valid game resolution first.", "error");
            return;
        }
        ApplyResolution(width, height);
        SendToast($"Resolution set to {width} x {height}.", "success");
    }

    private void OnStart()
    {
        if (!Input.IsReady)
        {
            SendToast("ViGEm input is unavailable. Reconnect the virtual controller before starting.", "error");
            return;
        }

        if (!TryReadResolution(out int width, out int height))
        {
            SendToast("Set a valid resolution before starting.", "error");
            return;
        }
        ApplyResolution(width, height);
        SystemSounds.Beep.Play();
        _bot.Start();
        SendToast("DANBOT is running.", "success");
        SendStatus();
    }

    private bool TryReadResolution(out int width, out int height)
    {
        width = int.TryParse(_bot.S.Res1, out int parsedWidth) ? parsedWidth : 0;
        height = int.TryParse(_bot.S.Res2, out int parsedHeight) ? parsedHeight : 0;
        return width > 0 && height > 0 && width >= height;
    }

    private void OnScan()
    {
        if (TryReadResolution(out int width, out int height)) ApplyResolution(width, height);
        SendDialog("Screen scan", _bot.DebugScan(), "DIAGNOSTICS");
    }

    private void OnTestInput()
    {
        if (_bot.IsRunning && !_bot.IsPaused)
        {
            SendToast("Pause the bot before testing input.", "error");
            return;
        }

        (string name, Action press) = Tests[_testMode];
        _testMode = (_testMode + 1) % Tests.Length;
        _testCts.Cancel();
        _testCts.Dispose();
        _testCts = new CancellationTokenSource();
        var token = _testCts.Token;
        SendToast($"Testing {name} in 3 seconds.", "info");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, token);
                for (int i = 0; i < 5 && !token.IsCancellationRequested; i++)
                {
                    press();
                    Thread.Sleep(250);
                }
                SendToast($"Tested {name}.", "success");
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void OnApply()
    {
        SendSettings();
        SendToast("Settings applied.", "success");
    }

    private void OnLoad()
    {
        bool parryEnabled = false;
        _bot.UpdateSettings(s =>
        {
            foreach (string key in EditKeys)
            {
                string value = Config.Read(key);
                if (value.Length == 0) continue;
                SetEdit(s, key, value);
            }
            foreach (string key in CheckKeys)
                SetCheck(s, key, Config.Read(key) == "1");
            NormalizeHeroSelection(s);
            if (s.Ch("Peacekeeper"))
            {
                _prevLeftDeflect = s.Left;
                _prevRightDeflect = s.Right;
                s.Left = PeacekeeperDeflectDelayMs;
                s.Right = PeacekeeperDeflectDelayMs;
                _peacekeeperApplied = true;
            }
            else
            {
                _peacekeeperApplied = false;
            }
            parryEnabled = s.Parry;
        });
        _bot.ParryToggle = parryEnabled;
        SendSettings();
        SendToast("Settings loaded.", "success");
    }

    private void OnSave()
    {
        Settings s = _bot.S;
        foreach (string key in EditKeys) Config.Write(key, GetEdit(s, key));
        foreach (string key in CheckKeys) Config.Write(key, GetCheck(s, key) ? "1" : "0");
        SendToast("Settings saved.", "success");
    }

    private static string GetEdit(Settings s, string key)
    {
        return key switch
        {
            "res1" => s.Res1,
            "res2" => s.Res2,
            "Pause" => s.Pause.ToString(),
            "Pause1" => s.Pause1.ToString(),
            "Pause2" => s.Pause2.ToString(),
            "Pause3" => s.Pause3.ToString(),
            "ParryDelay" => s.ParryDelay.ToString(),
            "GuardHold" => s.GuardHold.ToString(),
            "Left" => s.Left.ToString(),
            "Right" => s.Right.ToString(),
            _ => ""
        };
    }

    private static void SetEdit(Settings s, string key, string value)
    {
        switch (key)
        {
            case "res1": s.Res1 = value; break;
            case "res2": s.Res2 = value; break;
            case "Pause": s.Pause = ClampDelay(ToInt(value)); break;
            case "Pause1": s.Pause1 = ClampDelay(ToInt(value)); break;
            case "Pause2": s.Pause2 = ClampDelay(ToInt(value)); break;
            case "Pause3": s.Pause3 = ClampDelay(ToInt(value)); break;
            case "ParryDelay": s.ParryDelay = ClampDelay(ToInt(value)); break;
            case "GuardHold": s.GuardHold = Math.Clamp(ToInt(value), 60, MaxDelayMs); break;
            case "Left": s.Left = ClampDelay(ToInt(value)); break;
            case "Right": s.Right = ClampDelay(ToInt(value)); break;
        }
    }

    private static int ToInt(string value) => int.TryParse(value, out int number) ? number : 0;

    private void SendSettings()
    {
        SendToUi(new { type = "settings", settings = SettingsSnapshot() });
    }

    private void SendToast(string message, string kind)
    {
        SendToUi(new { type = "toast", message, kind });
    }

    private void SendDialog(string title, string body, string eyebrow)
    {
        SendToUi(new { type = "dialog", title, body, eyebrow });
    }

    private void SendToUi(object message)
    {
        if (InvokeRequired)
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => SendToUi(message)));
            return;
        }
        if (!_webReady || _webView.CoreWebView2 == null || IsDisposed) return;
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void ApplyResolution(int width, int height)
    {
        _bot.B55 = width / 1920.0;
        _bot.Y55 = height / 1080.0;
        _bot.X8 = (width / 1920.0) * 860;
        _bot.Y8 = (height / 1080.0) * 80;
        _bot.X9 = (width / 1920.0) * 1075;
        _bot.Y9 = (height / 1080.0) * 425;
        _bot.X18 = (width / 1920.0) * 670;
        _bot.Y18 = (height / 1080.0) * 300;
        _bot.X19 = (width / 1920.0) * 820;
        _bot.Y19 = (height / 1080.0) * 510;
        _bot.RefreshVisionSnapshot();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        for (int i = 0; i < 7; i++)
        {
            Native.RegisterHotKey(Handle, i + 1, 0, (uint)(0x70 + i));
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        for (int i = 0; i < 7; i++)
        {
            Native.UnregisterHotKey(Handle, i + 1);
        }
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statusTimer.Stop();
        _testCts.Cancel();
        _bot.StopTelemetry();
        _bot.Stop();
        _hook.Dispose();
        _visionOverlay.Dispose();
        _webView.Dispose();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            HandleHotkey(m.WParam.ToInt32());
            return;
        }
        base.WndProc(ref m);
    }

    private void OnKey(int vk, bool down)
    {
        if (vk == Input.VK_E)
        {
            _bot.EHeld = down;
        }
        else if (vk == Input.VK_F)
        {
            _fKeyDown = down;
            _bot.FHeld = _fKeyDown || Input.HoldButtonHeld();
        }
        else if (vk == Input.VK_LSHIFT)
        {
            if (down && !_shiftDown && _bot.S.Active12 == 1)
                Input.KeyTap(Input.VK_NUMPAD3);
            _shiftDown = down;
        }
        else if (vk == Input.VK_R)
        {
            if (down && !_rDown)
            {
                _bot.DodgeEnabled = false;
                _bot.ScheduleRele();
            }
            _rDown = down;
        }
    }

    private void HandleHotkey(int id)
    {
        switch (id)
        {
            case IdF1:
                _bot.UpdateSettings(next => next.Pause = next.Pause == 0 ? 80 : 0);
                SendSettings();
                Sound(_bot.S.Pause == 0 ? "buttonunclick" : "buttonclick");
                break;
            case IdF2:
                _bot.DodgeEnabled = !_bot.DodgeEnabled;
                Sound(_bot.DodgeEnabled ? "buttonclick" : "buttonunclick");
                break;
            case IdF3:
                string fMode = "Parry";
                _bot.UpdateSettings(next =>
                {
                    if (next.Parry) { next.Parry = false; next.Crushing = true; next.Deflect = false; fMode = "Crushing counter"; }
                    else if (next.Crushing) { next.Parry = false; next.Crushing = false; next.Deflect = true; fMode = "Deflect"; }
                    else { next.Parry = true; next.Crushing = false; next.Deflect = false; fMode = "Parry"; }
                    _bot.ParryToggle = next.Parry;
                });
                SendSettings();
                SendToast($"F-mode: {fMode}", "info");
                Sound("buttonclick");
                break;
            case IdF4:
                string eMode = "Parry";
                _bot.UpdateSettings(next =>
                {
                    if (next.Parry2) { next.Parry2 = false; next.Crushing2 = true; eMode = "Crushing counter"; }
                    else if (next.Crushing2) { next.Parry2 = false; next.Crushing2 = false; eMode = "Off"; }
                    else { next.Parry2 = true; next.Crushing2 = false; eMode = "Parry"; }
                });
                SendSettings();
                SendToast($"E-mode: {eMode}", "info");
                Sound("buttonclick");
                break;
            case IdF5:
                ToggleOrangeParry();
                break;
            case IdF6:
                _bot.TogglePause();
                Sound("buttonclick");
                break;
            case IdF7:
                ToggleVisionOverlay();
                return;
        }
        SendStatus();
    }

    private void ToggleOrangeParry()
    {
        _bot.OrangeParry = !_bot.OrangeParry;
        SendToast(_bot.OrangeParry ? "Orange parry ON" : "Orange parry OFF", _bot.OrangeParry ? "success" : "info");
        SendStatus();
    }

    private void ToggleVisionOverlay()
    {
        if (_visionOverlayVisible)
        {
            _visionOverlay.HideOverlay();
            _visionOverlayVisible = false;
            SendToast("Vision overlay hidden.", "info");
        }
        else if (_visionOverlay.TryShowOverlay())
        {
            _visionOverlayVisible = true;
            SendToast("Vision overlay enabled. Press F7 to hide it.", "success");
        }
        else
        {
            _visionOverlayVisible = false;
            SendToast("Vision overlay could not be excluded from screen capture, so it stayed off.", "error");
        }
        SendStatus();
    }

    private void ToggleAnchorScan()
    {
        _showAnchorScan = !_showAnchorScan;
        _visionOverlay.SetAnchorScanVisible(_showAnchorScan);
        SendToast(_showAnchorScan ? "Anchor scan shown." : "Anchor scan hidden.", "info");
        SendStatus();
    }

    private void ToggleTelemetry(string label)
    {
        if (_bot.Telemetry.Recording)
        {
            _bot.StopTelemetry();
            SendToast("Telemetry saved locally.", "success");
        }
        else
        {
            _bot.StartTelemetry(label);
            SendToast($"Telemetry recording: {label}.", "info");
        }
        SendStatus();
    }

    private void ExportTelemetry()
    {
        if (_bot.ExportTelemetry(this, out string result))
            SendToast("Telemetry ZIP exported.", "success");
        else
            SendToast(result, "info");
    }

    private static void Sound(string name)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, name + ".wav");
            if (File.Exists(path))
                using (var player = new SoundPlayer(path)) player.Play();
        }
        catch
        {
        }
    }

    private void DragWindow()
    {
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private const string HowToText =
        "IN GAME SETTINGS\n\n" +
        "1) Set FOV to 81 and contrast to 55.\n" +
        "2) Disable shadows, motion blur, ambient occlusion, dynamic reflections, and dynamic shadows.\n" +
        "3) Match the menu resolution to the game render.\n" +
        "4) Use fullscreen or borderless fullscreen, not windowed mode.\n" +
        "5) Hide physical/source controllers with HidHide and leave the ViGEm output visible.\n" +
        "6) Hold E or F before an attack for parry/counter actions. Orange handling runs automatically when enabled; F5 toggles orange parry.\n" +
        "7) F7 toggles the diagnostic vision overlay. It is click-through and does not change bot behavior.";

    private const string ReadMeText =
        "FEATURES\n\n" +
        "- Screen-aware orange and red indicator detection\n" +
        "- Orange-only dodge and optional orange plus red RT parry\n" +
        "- Auto block and directional guard\n" +
        "- Hero-specific evades and reactions\n" +
        "- ViGEm source/output merge\n" +
        "- Configurable reaction delays\n" +
        "- F7 anchor-following vision overlay\n" +
        "\nDANBOT by Danteh. The UI is a WebView2 shell over the existing C# bot core.";
}
