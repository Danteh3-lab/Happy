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
    private static readonly (ushort Mask, string Name)[] ControllerButtonBindings =
    {
        (0x0001, "DPad Up"), (0x0002, "DPad Down"), (0x0004, "DPad Left"), (0x0008, "DPad Right"),
        (0x0010, "Start"), (0x0020, "Back"), (0x0040, "LS"), (0x0080, "RS"),
        (0x0100, "LB"), (0x0200, "RB"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y")
    };
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

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
    private readonly ProfileEditorController _editor = new(ProfileStore.ForApplication());
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
    private bool _directSourceWarningShown;
    private ushort _autoDodgeBindBaselineButtons;
    private bool _autoDodgeBindBaselineLt;
    private bool _autoDodgeBindBaselineRt;
    private ushort _previousControllerButtons;
    private bool _previousControllerLt;
    private bool _previousControllerRt;
    private bool _controllerStateInitialized;

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
        _editor.Initialize(screen.Width.ToString(), screen.Height.ToString());
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

            if (!_bot.IsRunning || _bot.IsPaused)
                _bot.RecordBridgeHeartbeat();

            InputBridgeSnapshot bridge = ViGEmInput.GetDiagnostics();
            if (_webReady && bridge.SourceType == "direct-ds4" &&
                bridge.OtherXInputControllers > 0 && !_directSourceWarningShown)
            {
                _directSourceWarningShown = true;
                SendToast("Direct DS4 mode detected another XInput controller. Close DS4Windows or other virtual-pad tools.", "warning");
            }

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
            profile = _editor.ActiveProfile,
            profileDirty = _editor.IsDirty,
            profiles = _editor.ProfileNames(),
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
            sourceType = ViGEmInput.SourceType,
            virtualState = ViGEmInput.IsAvailable ? "ON" : "OFF",
            bridge = ViGEmInput.GetDiagnostics(),
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

    private Dictionary<string, object> SettingsSnapshot() => SettingsCodec.ToSnapshot(_editor.EditorSettings);

    private void ApplySettings(JsonElement values)
    {
        Settings editor = SettingsCodec.ApplyJson(_editor.EditorSettings, values);
        _editor.ReplaceEditor(editor);
        _bot.UpdateSettings(s => s.CopyLiveSwitchesFrom(editor));
        _bot.OrangeParry = editor.OrangeParry;
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

    private bool TryReadResolution(out int width, out int height) =>
        SettingsCodec.TryParseResolution(_editor.EditorSettings, out width, out height);

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
        string error = _editor.TryLoadActive(discard, draftDirty);
        if (error != null)
        {
            SendStatus();
            SendToast(error, "error");
            return;
        }
        SendSettings();
        SendStatus();
        SendToast($"Profile loaded: {_editor.ActiveProfile}.", "success");
    }

    private void OnProfileSelect(string profileName, bool discard, bool draftDirty)
    {
        string error = _editor.Select(profileName, discard, draftDirty);
        if (error != null)
        {
            SendToast(error, "error");
            SendStatus();
            return;
        }

        SendSettings();
        SendStatus();
        SendToast($"Profile loaded: {_editor.ActiveProfile}.", "success");
    }

    private void OnSave()
    {
        _editor.SaveActive();
        SendStatus();
        SendToast($"Profile saved: {_editor.ActiveProfile}.", "success");
    }

    private void OnProfileSaveAs(string profileName)
    {
        string error = _editor.SaveAs(profileName);
        if (error != null)
        {
            SendToast(error, "error");
            return;
        }
        SendSettings();
        SendStatus();
        SendToast($"Profile saved as: {_editor.ActiveProfile}.", "success");
    }

    private void OnProfileDelete(string profileName, bool discard, bool draftDirty)
    {
        string error = _editor.Delete(profileName, discard, draftDirty);
        if (error != null)
        {
            if (error == ProfileEditorController.DiscardBeforeDelete) SendStatus();
            SendToast(error, "error");
            return;
        }
        SendSettings();
        SendStatus();
        // Validated inside Delete, so this cannot throw on the success path.
        SendToast($"Profile deleted: {ProfileStore.NormalizeProfileName(profileName)}.", "success");
    }

    private void CommitEditorSettings()
    {
        Settings snapshot = _editor.EditorSettings.Clone();
        SettingsCodec.ApplyPeacekeeperRuntimeOverride(snapshot);
        _bot.UpdateSettings(s => s.CopyFrom(snapshot));
        _bot.OrangeParry = _editor.EditorSettings.OrangeParry;
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
        _bot.UpdateResolution(width, height);
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
                    live => live.Pause = _editor.EditorSettings.Pause);
                SendSettings();
                Sound(_editor.EditorSettings.Pause == 0 ? "buttonunclick" : "buttonclick");
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
                        live.Parry = _editor.EditorSettings.Parry;
                        live.Crushing = _editor.EditorSettings.Crushing;
                        live.Deflect = _editor.EditorSettings.Deflect;
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
                        live.Parry2 = _editor.EditorSettings.Parry2;
                        live.Crushing2 = _editor.EditorSettings.Crushing2;
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
        Settings next = _editor.EditorSettings.Clone();
        next.OrangeParry = enabled;
        _editor.ReplaceEditor(next);
        _bot.UpdateSettings(s => s.OrangeParry = enabled);
        SendToast(_bot.OrangeParry ? "Orange parry ON" : "Orange parry OFF", _bot.OrangeParry ? "success" : "info");
        SendSettings();
        SendStatus();
    }

    private void MutateEditorAndLive(Action<Settings> updateEditor, Action<Settings> updateLive)
    {
        Settings next = _editor.EditorSettings.Clone();
        updateEditor(next);
        _editor.ReplaceEditor(next);
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
                    live => live.AutoDodgeBind = _editor.EditorSettings.AutoDodgeBind);
                _bindingAutoDodge = false;
                SendSettings();
                SendToast($"Auto dodge bound to {pressed}.", "success");
                SendStatus();
            }
        }
        else if (!string.IsNullOrWhiteSpace(_editor.EditorSettings.AutoDodgeBind) &&
                 string.Equals(pressed, _editor.EditorSettings.AutoDodgeBind, StringComparison.OrdinalIgnoreCase))
        {
            bool enabled = false;
            MutateEditorAndLive(
                s =>
                {
                    s.Unblockables = !s.Unblockables;
                    enabled = s.Unblockables;
                },
                live => live.Unblockables = _editor.EditorSettings.Unblockables);
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
