using System.Media;
using System.Runtime.InteropServices;
using System.Text.Json;
using HappyBot.Combat;
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
    private ControllerToggleAction _bindingControllerAction;
    private bool _directSourceWarningShown;
    private ushort _controllerBindBaselineButtons;
    private bool _controllerBindBaselineLt;
    private bool _controllerBindBaselineRt;
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
        CommitEditorSettings();
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
                    OnStart(root.TryGetProperty("requestId", out JsonElement startRequestId) ? startRequestId.GetString() ?? "" : "");
                    break;
                case "toggle-pause":
                    OnTogglePause();
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
                    OnApply(root.TryGetProperty("requestId", out JsonElement requestId) ? requestId.GetString() ?? "" : "");
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
                    ToggleControllerBindingCapture(ControllerToggleAction.AutoDodge);
                    break;
                case "bind-orange-parry":
                    ToggleControllerBindingCapture(ControllerToggleAction.OrangeParry);
                    break;
                case "bind-auto-parry":
                    ToggleControllerBindingCapture(ControllerToggleAction.AutoParry);
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
        VisionSnapshot vision = _bot.GetVisionSnapshot();
        return new
        {
            version = BuildInfo.Version,
            build = BuildInfo.Configuration,
            profile = _editor.ActiveProfile,
            profileDirty = _editor.IsDirty,
            timingsDirty = HasUnappliedTimings(),
            behavior = ReactionBehaviorSummary.Create(_bot.S, Input.CanSendBulwark),
            profiles = _editor.ProfileNames(),
            running = _bot.IsRunning,
            paused = _bot.IsPaused,
            error = _bot.LastError,
            marker = _bot.MarkerFound ? "FOUND" : "MISSING",
            hold = _bot.FHeld ? "DOWN" : "UP",
            indicator = _bot.AttackIndicator ? "YES" : "NO",
            guard = _bot.GuardDir,
            flash = _bot.Flash ? "YES" : "NO",
            lastReaction = new
            {
                state = vision.LastReactionState,
                reason = vision.LastReactionReason,
                direction = vision.LastReactionDirection,
                delayMs = vision.LastReactionDelayMs,
                confirmation = ReactionConfirmationLabel(vision.LastReactionState)
            },
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
            autoDodgeBind = BindingStatus(ControllerToggleAction.AutoDodge),
            orangeParryBind = BindingStatus(ControllerToggleAction.OrangeParry),
            autoParryBind = BindingStatus(ControllerToggleAction.AutoParry),
            bindingAutoDodge = _bindingControllerAction == ControllerToggleAction.AutoDodge,
            bindingOrangeParry = _bindingControllerAction == ControllerToggleAction.OrangeParry,
            bindingAutoParry = _bindingControllerAction == ControllerToggleAction.AutoParry,
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

    private static string ReactionConfirmationLabel(string state)
    {
        if (state.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return "WAITING";
        if (state.Contains("UNCONFIRMED", StringComparison.OrdinalIgnoreCase)) return "UNCONFIRMED";
        if (state.Contains("CONFIRMED", StringComparison.OrdinalIgnoreCase)) return "CONFIRMED";
        if (state.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("BLOCKED", StringComparison.OrdinalIgnoreCase)) return "NOT DELIVERED";
        if (state.Contains("SENT", StringComparison.OrdinalIgnoreCase)) return "INPUT SENT";
        if (state.Contains("READY", StringComparison.OrdinalIgnoreCase)) return "PENDING";
        return "OBSERVED";
    }

    private string BindingStatus(ControllerToggleAction action)
    {
        string binding = ControllerToggleBindings.Get(_editor.EditorSettings, action);
        return string.IsNullOrWhiteSpace(binding) ? "UNBOUND" : binding;
    }

    private Dictionary<string, object> SettingsSnapshot() => SettingsCodec.ToSnapshot(_editor.EditorSettings);

    private void ApplySettings(JsonElement values)
    {
        Settings editor = SettingsCodec.ApplyJson(_editor.EditorSettings, values);
        _editor.ReplaceEditor(editor);
        _bot.UpdateSettings(s => s.CopyLiveSwitchesFrom(editor));
        _bot.OrangeParry = editor.OrangeParry;
        SendStatus();
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

    private void OnStart(string requestId)
    {
        if (!Input.IsReady)
        {
            const string message = "ViGEm input is unavailable. Reconnect the virtual controller before starting.";
            SendToUi(new { type = "apply-result", requestId, success = false, message });
            SendToast(message, "error");
            return;
        }

        if (!TryReadResolution(out int width, out int height))
        {
            const string message = "Set a valid resolution before starting.";
            SendToUi(new { type = "apply-result", requestId, success = false, message });
            SendToast(message, "error");
            return;
        }
        CommitEditorSettings();
        ApplyResolution(width, height);
        SystemSounds.Beep.Play();
        _bot.Start();
        SendToUi(new { type = "apply-result", requestId, success = true, message = "Timings applied." });
        SendToast("DANBOT is running.", "success");
        SendStatus();
    }

    private void OnTogglePause()
    {
        if (!_bot.IsRunning) return;
        _bot.TogglePause();
        SendToast(_bot.IsPaused ? "DANBOT paused." : "DANBOT resumed.", "info");
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

    private void OnApply(string requestId)
    {
        try
        {
            if (!TryReadResolution(out int width, out int height))
            {
                const string invalidMessage = "Set a valid resolution before applying settings.";
                SendToUi(new { type = "apply-result", requestId, success = false, message = invalidMessage });
                SendStatus();
                SendToast(invalidMessage, "error");
                return;
            }
            CommitEditorSettings();
            ApplyResolution(width, height);
            SendSettings();
            SendStatus();
            SendToUi(new { type = "apply-result", requestId, success = true, message = "Timings applied." });
            SendToast("Timings applied.", "success");
        }
        catch (Exception ex)
        {
            string failureMessage = "Timings were not applied: " + ex.Message;
            SendToUi(new { type = "apply-result", requestId, success = false, message = failureMessage });
            SendStatus();
            SendToast(failureMessage, "error");
        }
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
        _bot.UpdateSettings(s => s.CopyLiveSwitchesFrom(_editor.EditorSettings));
        _bot.OrangeParry = _editor.EditorSettings.OrangeParry;
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
        _bot.UpdateSettings(s => s.CopyLiveSwitchesFrom(_editor.EditorSettings));
        _bot.OrangeParry = _editor.EditorSettings.OrangeParry;
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
        _bot.UpdateSettings(s => s.CopyLiveSwitchesFrom(_editor.EditorSettings));
        _bot.OrangeParry = _editor.EditorSettings.OrangeParry;
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

    private bool HasUnappliedTimings()
    {
        Settings editor = _editor.EditorSettings.Clone();
        SettingsCodec.ApplyPeacekeeperRuntimeOverride(editor);
        Settings applied = _bot.S;
        return editor.Pause != applied.Pause || editor.Pause1 != applied.Pause1 ||
            editor.Pause2 != applied.Pause2 || editor.Pause3 != applied.Pause3 ||
            editor.ParryDelay != applied.ParryDelay || editor.LegitParryChance != applied.LegitParryChance ||
            editor.CrushingFallbackChance != applied.CrushingFallbackChance ||
            editor.DeflectFallbackChance != applied.DeflectFallbackChance || editor.GuardHold != applied.GuardHold ||
            editor.Left != applied.Left || editor.Right != applied.Right || editor.TopDeflect != applied.TopDeflect;
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

    private void ToggleControllerBindingCapture(ControllerToggleAction action)
    {
        string displayName = ControllerToggleBindings.DisplayName(action);
        if (_bindingControllerAction == action)
        {
            _bindingControllerAction = ControllerToggleAction.None;
            SendToast($"{displayName} binding cancelled.", "info");
            SendStatus();
            return;
        }

        if (!ViGEmInput.SourceConnected || !ViGEmInput.TryGetSourceState(out Native.XINPUT_GAMEPAD source))
        {
            SendToast($"Connect the physical source controller before binding {displayName.ToLowerInvariant()}.", "error");
            return;
        }

        _controllerBindBaselineButtons = source.wButtons;
        _controllerBindBaselineLt = source.bLeftTrigger > 32;
        _controllerBindBaselineRt = source.bRightTrigger > 32;
        _bindingControllerAction = action;
        SendToast($"Press a controller button to bind {displayName.ToLowerInvariant()}.", "info");
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
            _bindingControllerAction != ControllerToggleAction.None ? _controllerBindBaselineButtons : _previousControllerButtons,
            _bindingControllerAction != ControllerToggleAction.None ? _controllerBindBaselineLt : _previousControllerLt,
            _bindingControllerAction != ControllerToggleAction.None ? _controllerBindBaselineRt : _previousControllerRt);

        if (_bindingControllerAction != ControllerToggleAction.None)
        {
            if (!string.IsNullOrEmpty(pressed))
            {
                ControllerToggleAction boundAction = _bindingControllerAction;
                MutateEditorAndLive(
                    s => ControllerToggleBindings.Assign(s, boundAction, pressed),
                    live => ControllerToggleBindings.Copy(live, _editor.EditorSettings));
                _bindingControllerAction = ControllerToggleAction.None;
                SendSettings();
                SendToast($"{ControllerToggleBindings.DisplayName(boundAction)} bound to {pressed}.", "success");
                SendStatus();
            }
        }
        else
        {
            DispatchControllerToggle(ControllerToggleBindings.Resolve(_editor.EditorSettings, pressed));
        }

        _previousControllerButtons = source.wButtons;
        _previousControllerLt = source.bLeftTrigger > 32;
        _previousControllerRt = source.bRightTrigger > 32;
    }

    private void DispatchControllerToggle(ControllerToggleAction action)
    {
        switch (action)
        {
            case ControllerToggleAction.AutoDodge:
                ToggleAutoDodge();
                break;
            case ControllerToggleAction.OrangeParry:
                ToggleOrangeParry();
                break;
            case ControllerToggleAction.AutoParry:
                ToggleAutoParry();
                break;
        }
    }

    private void ToggleAutoDodge()
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

    private void ToggleAutoParry()
    {
        bool enabled = false;
        MutateEditorAndLive(
            s =>
            {
                s.Parry = !s.Parry;
                enabled = s.Parry;
            },
            live => live.Parry = _editor.EditorSettings.Parry);
        SendSettings();
        SendToast(enabled ? "F auto parry ON." : "F auto parry OFF.", enabled ? "success" : "info");
        SendStatus();
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
        "6) Hold E or F before an attack for parry/counter actions. Orange handling runs automatically when enabled; own controller RT/RB attacks are ignored until their orange clears. Diagnostics can bind controller buttons for Auto dodge, Orange parry, and F auto parry; F5 still toggles orange parry.\n" +
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
