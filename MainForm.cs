using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HappyBot;

public sealed class MainForm : Form
{
    private const int IdF1 = 1, IdF2 = 2, IdF3 = 3, IdF4 = 4, IdF6 = 6;
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    private static readonly string[] EditKeys = { "res1", "res2", "Pause", "Pause1", "Pause2", "Pause3", "Left", "Right" };

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
    private readonly System.Windows.Forms.Timer _statusTimer;
    private CancellationTokenSource _testCts = new();
    private int _testMode;
    private bool _shiftDown;
    private bool _rDown;
    private bool _fKeyDown;
    private bool _webReady;

    public MainForm()
    {
        Text = "DANBOT Control Deck";
        BackColor = Color.FromArgb(12, 12, 15);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 620);

        var screen = Screen.PrimaryScreen.Bounds;
        _bot.S.Res1 = screen.Width.ToString();
        _bot.S.Res2 = screen.Height.ToString();
        ApplyResolution(screen.Width, screen.Height);

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
            dodgeEnabled = _bot.S.Active1 == 1,
            legit = _bot.S.Legit
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
            case "Leftdodge": s.Leftdodge = value; break;
            case "Rightdodge": s.Rightdodge = value; break;
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
        var s = _bot.S;
        s.Res1 = ReadString(values, "res1", s.Res1);
        s.Res2 = ReadString(values, "res2", s.Res2);
        s.Pause = ReadInt(values, "Pause", s.Pause);
        s.Pause1 = ReadInt(values, "Pause1", s.Pause1);
        s.Pause2 = ReadInt(values, "Pause2", s.Pause2);
        s.Pause3 = ReadInt(values, "Pause3", s.Pause3);
        s.Left = ReadInt(values, "Left", s.Left);
        s.Right = ReadInt(values, "Right", s.Right);
        foreach (string key in CheckKeys)
        {
            if (values.TryGetProperty(key, out _))
                SetCheck(s, key, ReadBool(values, key, GetCheck(s, key)));
        }
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
        foreach (string key in EditKeys)
        {
            string value = Config.Read(key);
            if (value.Length == 0) continue;
            SetEdit(_bot.S, key, value);
        }
        foreach (string key in CheckKeys)
            SetCheck(_bot.S, key, Config.Read(key) == "1");
        SendSettings();
        SendToast("Settings loaded.", "success");
    }

    private void OnSave()
    {
        foreach (string key in EditKeys) Config.Write(key, GetEdit(_bot.S, key));
        foreach (string key in CheckKeys) Config.Write(key, GetCheck(_bot.S, key) ? "1" : "0");
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
            case "Pause": s.Pause = ToInt(value); break;
            case "Pause1": s.Pause1 = ToInt(value); break;
            case "Pause2": s.Pause2 = ToInt(value); break;
            case "Pause3": s.Pause3 = ToInt(value); break;
            case "Left": s.Left = ToInt(value); break;
            case "Right": s.Right = ToInt(value); break;
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
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        for (int i = 0; i < 6; i++)
        {
            if (i == 4) continue;
            Native.RegisterHotKey(Handle, i + 1, 0, (uint)(0x70 + i));
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        for (int i = 0; i < 6; i++)
        {
            if (i == 4) continue;
            Native.UnregisterHotKey(Handle, i + 1);
        }
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statusTimer.Stop();
        _testCts.Cancel();
        _bot.Stop();
        _hook.Dispose();
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
                _bot.S.Active1 = 0;
                _bot.ScheduleRele();
            }
            _rDown = down;
        }
    }

    private void HandleHotkey(int id)
    {
        var s = _bot.S;
        switch (id)
        {
            case IdF1:
                if (s.Pause == 0) { s.Pause = 80; Sound("buttonclick"); }
                else { s.Pause = 0; Sound("buttonunclick"); }
                break;
            case IdF2:
                if (s.Active1 == 1) { s.Active1 = 0; Sound("buttonunclick"); }
                else { s.Active1 = 1; Sound("buttonclick"); }
                break;
            case IdF3:
                if (s.Active3 == 1) { s.Active3 = 0; s.Active4 = 1; Sound("buttonclick"); }
                else if (s.Active4 == 1) { s.Active3 = 1; s.Active4 = 0; Sound("buttonclick"); }
                break;
            case IdF4:
                s.NMode++;
                if (s.NMode == 1) { s.Active9 = 1; s.Active11 = 0; s.Active12 = 0; Sound("buttonclick"); }
                else if (s.NMode == 2) { s.Active9 = 0; s.Active11 = 1; s.Active12 = 0; Sound("buttonclick"); }
                else if (s.NMode == 3) { s.Active9 = 0; s.Active11 = 0; s.Active12 = 1; Sound("buttonclick"); s.NMode = 0; }
                break;
            case IdF6:
                _bot.TogglePause();
                Sound("buttonclick");
                break;
        }
        SendStatus();
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
        "6) Hold E or F before an attack for parry/counter actions. Hold LT for orange automation.";

    private const string ReadMeText =
        "FEATURES\n\n" +
        "- Screen-aware orange and red indicator detection\n" +
        "- Orange-only dodge and orange plus red RT parry\n" +
        "- Auto block and directional guard\n" +
        "- Hero-specific evades and reactions\n" +
        "- ViGEm source/output merge\n" +
        "- Configurable reaction delays\n" +
        "\nDANBOT by Danteh. The UI is a WebView2 shell over the existing C# bot core.";
}
