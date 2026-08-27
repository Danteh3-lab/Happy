using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HappyBot;

public sealed class MainForm : Form
{
    private const int IdF1 = 1, IdF3 = 3, IdF4 = 4, IdF5 = 5, IdF6 = 6, IdF7 = 7;
    private static readonly int[] HotkeyIds = { IdF1, IdF3, IdF4, IdF5, IdF6, IdF7 };
    private const int PeacekeeperDeflectDelayMs = 100;
    private const int MaxDelayMs = 10000;
    private static readonly (ushort Mask, string Name)[] ControllerButtonBindings =
    {
        (0x0001, "DPad Up"), (0x0002, "DPad Down"), (0x0004, "DPad Left"), (0x0008, "DPad Right"),
        (0x0010, "Start"), (0x0020, "Back"), (0x0040, "LS"), (0x0080, "RS"),
        (0x0100, "LB"), (0x0200, "RB"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")
    };
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    private static readonly string[] EditKeys = { "res1", "res2", "Pause", "Pause1", "Pause2", "Pause3", "ParryDelay", "LegitParryChance", "CrushingFallbackChance", "DeflectFallbackChance", "GuardHold", "Left", "Right", "TopDeflect", "AutoDodgeBind" };

    private static readonly string[] CheckKeys =
    {
        "DodgeL", "DodgeH", "Leftdodge", "Rightdodge", "Unblockables", "OrangeLight", "OrangeParry", "Autoblock", "Lightbash",
        "Parry", "Crushing", "Deflect", "Parry2", "Crushing2", "Nohero", "YourHero", "Legit", "BulwarkFallback",
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
    private readonly ProfileStore _profiles = ProfileStore.ForApplication();
    private readonly KeyboardHook _hook;
    private readonly WebView2 _webView;
    private readonly VisionOverlayForm _visionOverlay;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _controllerTimer;
    private CancellationTokenSource _testCts = new();
    private int _testMode;
    private bool _fKeyDown;
    private bool _webReady;
    private bool _visionOverlayVisible;
    private bool _showAnchorScan = true;
    private bool _bindingAutoDodge;
    private ushort _autoDodgeBindBaselineButtons;
    private bool _autoDodgeBindBaselineLt;
    private bool _autoDodgeBindBaselineRt;
    private ushort _previousControllerButtons;
    private bool _previousControllerLt;
    private bool _previousControllerRt;
    private bool _controllerStateInitialized;
    private Settings _editorSettings = new();
    private string _activeProfile = ProfileStore.DefaultProfileName;
    private bool _profileDirty;

    public MainForm()
    {
        Text = $"DANBOT Control Deck {BuildInfo.Display}";
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
        _editorSettings = _bot.S.Clone();
        string startupProfile = _profiles.ReadActiveProfile();
        if (!ProfileNames().Contains(startupProfile, StringComparer.OrdinalIgnoreCase))
            startupProfile = ProfileStore.DefaultProfileName;
        LoadProfileIntoEditor(startupProfile);
        ApplyResolution(screen.Width, screen.Height);

        _visionOverlay = new VisionOverlayForm(_bot.GetVisionSnapshot, _bot.GetOverlayFeatures);

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
        _controllerTimer = new System.Windows.Forms.Timer { Interval = 35 };
        _controllerTimer.Tick += (_, _) => PollControllerBinding();
        _controllerTimer.Start();
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
                    OnLoad(false, false);
                    break;
                case "apply":
                    OnApply();
                    break;
                case "profile-select":
                    OnProfileSelect(
                        root.TryGetProperty("name", out JsonElement profileName) ? profileName.GetString() ?? "" : "",
                        root.TryGetProperty("discard", out JsonElement discard) && discard.ValueKind == JsonValueKind.True,
                        root.TryGetProperty("draftDirty", out JsonElement selectDraftDirty) && selectDraftDirty.ValueKind == JsonValueKind.True);
                    break;
                case "profile-load":
                    OnLoad(
                        root.TryGetProperty("discard", out JsonElement loadDiscard) && loadDiscard.ValueKind == JsonValueKind.True,
                        root.TryGetProperty("draftDirty", out JsonElement loadDraftDirty) && loadDraftDirty.ValueKind == JsonValueKind.True);
                    break;
                case "profile-save":
                    OnSave();
                    break;
                case "profile-save-as":
                    OnProfileSaveAs(root.TryGetProperty("name", out JsonElement saveAsName) ? saveAsName.GetString() ?? "" : "");
                    break;
                case "profile-delete":
                    OnProfileDelete(
                        root.TryGetProperty("name", out JsonElement deleteName) ? deleteName.GetString() ?? "" : "",
                        root.TryGetProperty("discard", out JsonElement deleteDiscard) && deleteDiscard.ValueKind == JsonValueKind.True,
                        root.TryGetProperty("draftDirty", out JsonElement deleteDraftDirty) && deleteDraftDirty.ValueKind == JsonValueKind.True);
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
                case "bind-auto-dodge":
                    ToggleAutoDodgeBindingCapture();
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
            version = BuildInfo.Version,
            build = BuildInfo.Configuration,
            profile = _activeProfile,
            profileDirty = _profileDirty,
            profiles = ProfileNames(),
            running = _bot.IsRunning,
            error = _bot.LastError,
            marker = _bot.MarkerFound ? "FOUND" : "MISSING",
            hold = _bot.FHeld ? "DOWN" : "UP",
            indicator = _bot.AttackIndicator ? "YES" : "NO",
            guard = _bot.GuardDir,
            flash = _bot.Flash ? "YES" : "NO",
            parryCount = _bot.ParryCount,
            rtSent = _bot.ParryCount,
            parryAttempts = _bot.ParryCount,
            parriesConfirmed = _bot.ParryConfirmedCount,
            parriesUnconfirmed = _bot.ParryUnconfirmedCount,
            injected = Input.InjectedCount,
            mode = Input.ActiveMode,
            source = ViGEmInput.SourceConnected ? "ON" : "OFF",
            sourceSlot = ViGEmInput.SourceSlot,
            virtualState = ViGEmInput.IsAvailable ? "ON" : "OFF",
            loop = _bot.LoopHz,
            legit = _bot.S.Legit,
            orangeParry = _bot.OrangeParry,
            autoDodgeBind = string.IsNullOrWhiteSpace(_bot.S.AutoDodgeBind) ? "UNBOUND" : _bot.S.AutoDodgeBind,
            bindingAutoDodge = _bindingAutoDodge,
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
        var s = _editorSettings;
        var values = new Dictionary<string, object>
        {
            ["res1"] = s.Res1,
            ["res2"] = s.Res2,
            ["Pause"] = s.Pause,
            ["Pause1"] = s.Pause1,
            ["Pause2"] = s.Pause2,
            ["Pause3"] = s.Pause3,
            ["ParryDelay"] = s.ParryDelay,
            ["LegitParryChance"] = s.LegitParryChance,
            ["CrushingFallbackChance"] = s.CrushingFallbackChance,
            ["DeflectFallbackChance"] = s.DeflectFallbackChance,
            ["GuardHold"] = s.GuardHold,
            ["Left"] = s.Left,
            ["Right"] = s.Right,
            ["TopDeflect"] = s.TopDeflect,
            ["AutoDodgeBind"] = s.AutoDodgeBind
        };
        foreach (string key in CheckKeys) values[key] = GetCheck(s, key);
        return values;
    }

    private IReadOnlyList<string> ProfileNames()
    {
        try { return _profiles.ListProfiles(); }
        catch { return new[] { ProfileStore.DefaultProfileName }; }
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
            "OrangeLight" => s.OrangeLight,
            "OrangeParry" => s.OrangeParry,
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
            "BulwarkFallback" => s.BulwarkFallback,
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
            case "OrangeLight": s.OrangeLight = value; break;
            case "OrangeParry": s.OrangeParry = value; break;
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
            case "BulwarkFallback": s.BulwarkFallback = value; break;
            default: s.Chars[key] = value; break;
        }
    }

    private void ApplySettings(JsonElement values)
    {
        Settings editor = _editorSettings.Clone();
        editor.Res1 = ReadString(values, "res1", editor.Res1);
        editor.Res2 = ReadString(values, "res2", editor.Res2);
        editor.Pause = ClampDelay(ReadInt(values, "Pause", editor.Pause));
        editor.Pause1 = ClampDelay(ReadInt(values, "Pause1", editor.Pause1));
        editor.Pause2 = ClampDelay(ReadInt(values, "Pause2", editor.Pause2));
        editor.Pause3 = ClampDelay(ReadInt(values, "Pause3", editor.Pause3));
        editor.ParryDelay = ClampDelay(ReadInt(values, "ParryDelay", editor.ParryDelay));
        editor.LegitParryChance = Math.Clamp(ReadInt(values, "LegitParryChance", editor.LegitParryChance), 0, 100);
        editor.CrushingFallbackChance = Math.Clamp(ReadInt(values, "CrushingFallbackChance", editor.CrushingFallbackChance), 0, 100);
        editor.DeflectFallbackChance = Math.Clamp(ReadInt(values, "DeflectFallbackChance", editor.DeflectFallbackChance), 0, 100);
        editor.GuardHold = Math.Clamp(ReadInt(values, "GuardHold", editor.GuardHold), 60, MaxDelayMs);
        editor.Left = ClampDelay(ReadInt(values, "Left", editor.Left));
        editor.Right = ClampDelay(ReadInt(values, "Right", editor.Right));
        editor.TopDeflect = ClampDelay(ReadInt(values, "TopDeflect", editor.TopDeflect));
        editor.AutoDodgeBind = ReadString(values, "AutoDodgeBind", editor.AutoDodgeBind).Trim();
        foreach (string key in CheckKeys)
        {
            if (values.TryGetProperty(key, out _))
                SetCheck(editor, key, ReadBool(values, key, GetCheck(editor, key)));
        }

        NormalizeHeroSelection(editor);
        _editorSettings = editor;
        _profileDirty = true;
        _bot.UpdateSettings(s => s.CopyLiveSwitchesFrom(editor));
        _bot.OrangeParry = editor.OrangeParry;
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
        CommitEditorSettings();
        ApplyResolution(width, height);
        SystemSounds.Beep.Play();
        _bot.Start();
        SendToast("DANBOT is running.", "success");
        SendStatus();
    }

    private bool TryReadResolution(out int width, out int height)
    {
        width = int.TryParse(_editorSettings.Res1, out int parsedWidth) ? parsedWidth : 0;
        height = int.TryParse(_editorSettings.Res2, out int parsedHeight) ? parsedHeight : 0;
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
        if (!TryReadResolution(out int width, out int height))
        {
            SendToast("Set a valid resolution before applying settings.", "error");
            return;
        }
        CommitEditorSettings();
        ApplyResolution(width, height);
        SendSettings();
        SendStatus();
        SendToast("Settings applied.", "success");
    }

    private void OnLoad(bool discard, bool draftDirty)
    {
        if ((_profileDirty || draftDirty) && !discard)
        {
            SendStatus();
            SendToast("Discard unsaved profile changes before loading.", "error");
            return;
        }
        LoadProfileIntoEditor(_activeProfile);
        SendSettings();
        SendStatus();
        SendToast($"Profile loaded: {_activeProfile}.", "success");
    }

    private void OnProfileSelect(string profileName, bool discard, bool draftDirty)
    {
        string normalized;
        try
        {
            normalized = ProfileStore.NormalizeProfileName(profileName);
            if (!ProfileNames().Contains(normalized, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("That profile does not exist.", nameof(profileName));
        }
        catch (Exception ex)
        {
            SendToast(ex.Message, "error");
            SendStatus();
            return;
        }

        if ((_profileDirty || draftDirty) && !discard)
        {
            SendToast("Discard unsaved profile changes before switching.", "error");
            SendStatus();
            return;
        }

        LoadProfileIntoEditor(normalized);
        SendSettings();
        SendStatus();
        SendToast($"Profile loaded: {_activeProfile}.", "success");
    }

    private void OnSave()
    {
        SaveProfile(_activeProfile);
        SendStatus();
        SendToast($"Profile saved: {_activeProfile}.", "success");
    }

    private void OnProfileSaveAs(string profileName)
    {
        try
        {
            string normalized = ProfileStore.NormalizeProfileName(profileName);
            SaveProfile(normalized);
            _activeProfile = normalized;
            _profileDirty = false;
            SendSettings();
            SendStatus();
            SendToast($"Profile saved as: {_activeProfile}.", "success");
        }
        catch (Exception ex)
        {
            SendToast(ex.Message, "error");
        }
    }

    private void OnProfileDelete(string profileName, bool discard, bool draftDirty)
    {
        try
        {
            string normalized = ProfileStore.NormalizeProfileName(profileName);
            if (normalized.Equals(ProfileStore.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Default profile cannot be deleted.");
            if ((_profileDirty || draftDirty) && !discard)
            {
                SendStatus();
                SendToast("Discard unsaved profile changes before deleting.", "error");
                return;
            }
            _profiles.Delete(normalized);
            if (_activeProfile.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                LoadProfileIntoEditor(ProfileStore.DefaultProfileName);
            SendSettings();
            SendStatus();
            SendToast($"Profile deleted: {normalized}.", "success");
        }
        catch (Exception ex)
        {
            SendToast(ex.Message, "error");
        }
    }

    private void LoadProfileIntoEditor(string profileName)
    {
        string normalized = ProfileStore.NormalizeProfileName(profileName);
        Settings loaded = new()
        {
            Res1 = _editorSettings.Res1,
            Res2 = _editorSettings.Res2
        };
        foreach (string key in EditKeys)
        {
            string value = _profiles.Read(normalized, key);
            if (value.Length > 0) SetEdit(loaded, key, value);
        }
        foreach (string key in CheckKeys)
        {
            string value = _profiles.Read(normalized, key);
            if (value.Length > 0) SetCheck(loaded, key, value == "1");
        }

        NormalizeHeroSelection(loaded);
        _activeProfile = normalized;
        _editorSettings = loaded;
        _profileDirty = false;
        PersistActiveProfile();
    }

    private void SaveProfile(string profileName)
    {
        string normalized = ProfileStore.NormalizeProfileName(profileName);
        Settings s = _editorSettings;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in EditKeys) values[key] = GetEdit(s, key);
        foreach (string key in CheckKeys) values[key] = GetCheck(s, key) ? "1" : "0";
        _profiles.WriteAll(normalized, values);
        _activeProfile = normalized;
        _profileDirty = false;
        PersistActiveProfile();
    }

    private void PersistActiveProfile()
    {
        try
        {
            _profiles.WriteActiveProfile(_activeProfile);
        }
        catch
        {
            // Profile selection should remain usable even if metadata cannot be written.
        }
    }

    private void CommitEditorSettings()
    {
        Settings snapshot = _editorSettings.Clone();
        ApplyPeacekeeperRuntimeOverride(snapshot);
        _bot.UpdateSettings(s => s.CopyFrom(snapshot));
        _bot.OrangeParry = _editorSettings.OrangeParry;
    }

    private static void ApplyPeacekeeperRuntimeOverride(Settings settings)
    {
        if (!settings.Ch("Peacekeeper")) return;
        settings.Left = PeacekeeperDeflectDelayMs;
        settings.Right = PeacekeeperDeflectDelayMs;
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
            "LegitParryChance" => s.LegitParryChance.ToString(),
            "CrushingFallbackChance" => s.CrushingFallbackChance.ToString(),
            "DeflectFallbackChance" => s.DeflectFallbackChance.ToString(),
            "GuardHold" => s.GuardHold.ToString(),
            "Left" => s.Left.ToString(),
            "Right" => s.Right.ToString(),
            "TopDeflect" => s.TopDeflect.ToString(),
            "AutoDodgeBind" => s.AutoDodgeBind,
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
            case "LegitParryChance": s.LegitParryChance = Math.Clamp(ToInt(value), 0, 100); break;
            case "CrushingFallbackChance": s.CrushingFallbackChance = Math.Clamp(ToInt(value), 0, 100); break;
            case "DeflectFallbackChance": s.DeflectFallbackChance = Math.Clamp(ToInt(value), 0, 100); break;
            case "GuardHold": s.GuardHold = Math.Clamp(ToInt(value), 60, MaxDelayMs); break;
            case "Left": s.Left = ClampDelay(ToInt(value)); break;
            case "Right": s.Right = ClampDelay(ToInt(value)); break;
            case "TopDeflect": s.TopDeflect = ClampDelay(ToInt(value)); break;
            case "AutoDodgeBind": s.AutoDodgeBind = value.Trim(); break;
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
        foreach (int id in HotkeyIds)
        {
            Native.RegisterHotKey(Handle, id, 0, (uint)(0x70 + id - 1));
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        foreach (int id in HotkeyIds)
        {
            Native.UnregisterHotKey(Handle, id);
        }
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statusTimer.Stop();
        _controllerTimer.Stop();
        _testCts.Cancel();
        _testCts.Dispose();
        _bot.Dispose();
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
    }

    private void HandleHotkey(int id)
    {
        switch (id)
        {
            case IdF1:
                MutateEditorAndLive(
                    next => next.Pause = next.Pause == 0 ? 80 : 0,
                    live => live.Pause = _editorSettings.Pause);
                SendSettings();
                Sound(_editorSettings.Pause == 0 ? "buttonunclick" : "buttonclick");
                break;
            case IdF3:
                string fMode = "Parry";
                MutateEditorAndLive(
                    next =>
                    {
                        if (next.Parry) { next.Parry = false; next.Crushing = true; next.Deflect = false; fMode = "Crushing counter"; }
                        else if (next.Crushing) { next.Parry = false; next.Crushing = false; next.Deflect = true; fMode = "Deflect"; }
                        else { next.Parry = true; next.Crushing = false; next.Deflect = false; fMode = "Parry"; }
                    },
                    live =>
                    {
                        live.Parry = _editorSettings.Parry;
                        live.Crushing = _editorSettings.Crushing;
                        live.Deflect = _editorSettings.Deflect;
                    });
                SendSettings();
                SendToast($"F-mode: {fMode}", "info");
                Sound("buttonclick");
                break;
            case IdF4:
                string eMode = "Parry";
                MutateEditorAndLive(
                    next =>
                    {
                        if (next.Parry2) { next.Parry2 = false; next.Crushing2 = true; eMode = "Crushing counter"; }
                        else if (next.Crushing2) { next.Parry2 = false; next.Crushing2 = false; eMode = "Off"; }
                        else { next.Parry2 = true; next.Crushing2 = false; eMode = "Parry"; }
                    },
                    live =>
                    {
                        live.Parry2 = _editorSettings.Parry2;
                        live.Crushing2 = _editorSettings.Crushing2;
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
        bool enabled = !_bot.OrangeParry;
        _bot.OrangeParry = enabled;
        _editorSettings.OrangeParry = enabled;
        _profileDirty = true;
        _bot.UpdateSettings(s => s.OrangeParry = enabled);
        SendToast(_bot.OrangeParry ? "Orange parry ON" : "Orange parry OFF", _bot.OrangeParry ? "success" : "info");
        SendSettings();
        SendStatus();
    }

    private void MutateEditorAndLive(Action<Settings> updateEditor, Action<Settings> updateLive)
    {
        Settings next = _editorSettings.Clone();
        updateEditor(next);
        _editorSettings = next;
        _profileDirty = true;
        _bot.UpdateSettings(updateLive);
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

    private void ToggleAutoDodgeBindingCapture()
    {
        if (_bindingAutoDodge)
        {
            _bindingAutoDodge = false;
            SendToast("Auto dodge binding cancelled.", "info");
            SendStatus();
            return;
        }

        if (!ViGEmInput.SourceConnected || !ViGEmInput.TryGetSourceState(out Native.XINPUT_GAMEPAD source))
        {
            SendToast("Connect the physical source controller before binding auto dodge.", "error");
            return;
        }

        _autoDodgeBindBaselineButtons = source.wButtons;
        _autoDodgeBindBaselineLt = source.bLeftTrigger > 32;
        _autoDodgeBindBaselineRt = source.bRightTrigger > 32;
        _bindingAutoDodge = true;
        SendToast("Press a controller button to bind auto dodge.", "info");
        SendStatus();
    }

    private void PollControllerBinding()
    {
        if (!ViGEmInput.TryGetSourceState(out Native.XINPUT_GAMEPAD source))
        {
            _previousControllerButtons = 0;
            _previousControllerLt = false;
            _previousControllerRt = false;
            _controllerStateInitialized = false;
            return;
        }

        if (!_controllerStateInitialized)
        {
            _previousControllerButtons = source.wButtons;
            _previousControllerLt = source.bLeftTrigger > 32;
            _previousControllerRt = source.bRightTrigger > 32;
            _controllerStateInitialized = true;
            return;
        }

        string pressed = FindNewControllerBinding(
            source,
            _bindingAutoDodge ? _autoDodgeBindBaselineButtons : _previousControllerButtons,
            _bindingAutoDodge ? _autoDodgeBindBaselineLt : _previousControllerLt,
            _bindingAutoDodge ? _autoDodgeBindBaselineRt : _previousControllerRt);

        if (_bindingAutoDodge)
        {
            if (!string.IsNullOrEmpty(pressed))
            {
                MutateEditorAndLive(
                    s => s.AutoDodgeBind = pressed,
                    live => live.AutoDodgeBind = _editorSettings.AutoDodgeBind);
                _bindingAutoDodge = false;
                SendSettings();
                SendToast($"Auto dodge bound to {pressed}.", "success");
                SendStatus();
            }
        }
        else if (!string.IsNullOrWhiteSpace(_editorSettings.AutoDodgeBind) &&
                 string.Equals(pressed, _editorSettings.AutoDodgeBind, StringComparison.OrdinalIgnoreCase))
        {
            bool enabled = false;
            MutateEditorAndLive(
                s =>
                {
                    s.Unblockables = !s.Unblockables;
                    enabled = s.Unblockables;
                },
                live => live.Unblockables = _editorSettings.Unblockables);
            SendSettings();
            SendToast(enabled ? "Auto dodge ON." : "Auto dodge OFF.", enabled ? "success" : "info");
            SendStatus();
        }

        _previousControllerButtons = source.wButtons;
        _previousControllerLt = source.bLeftTrigger > 32;
        _previousControllerRt = source.bRightTrigger > 32;
    }

    private static string FindNewControllerBinding(Native.XINPUT_GAMEPAD current, ushort previousButtons, bool previousLt, bool previousRt)
    {
        foreach ((ushort mask, string name) in ControllerButtonBindings)
        {
            bool held = (current.wButtons & mask) != 0;
            if (held && (previousButtons & mask) == 0) return name;
        }

        bool currentLt = current.bLeftTrigger > 32;
        if (currentLt && !previousLt) return "LT";
        bool currentRt = current.bRightTrigger > 32;
        if (currentRt && !previousRt) return "RT";
        return "";
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
        "6) Hold E or F before an attack for parry/counter actions. Orange handling runs automatically when enabled; own controller RT/RB attacks are ignored until their orange clears; use the Dodge bind button to assign a controller toggle; F5 toggles orange parry.\n" +
        "7) F7 toggles the diagnostic vision overlay. It is click-through and does not change bot behavior.";

    private const string ReadMeText =
        "FEATURES\n\n" +
        "- Screen-aware orange and red indicator detection\n" +
        "- Orange-only dodge/light and optional orange plus red RT parry\n" +
        "- Own controller RT/RB attacks are excluded from orange responses\n" +
        "- Auto block and directional guard\n" +
        "- Hero-specific evades and reactions\n" +
        "- ViGEm source/output merge\n" +
        "- Configurable reaction delays\n" +
        "- F7 anchor-following vision overlay\n" +
        "\nDANBOT by Danteh. The UI is a WebView2 shell over the existing C# bot core.";
}
