using HappyBot.App.Protocol;

namespace HappyBot.App;

/// <summary>
/// Everything the control deck does, without any WinForms or WebView2 types.
/// The form forwards page commands and timer ticks here.
/// UI-thread only; background work reports back through <see cref="IUiDispatcher"/>.
/// </summary>
internal sealed class DeckController : IDisposable
{
    private readonly BotCore _bot;
    private readonly SettingsSession _session;
    private readonly IUiSink _ui;
    private readonly IUiDispatcher _dispatcher;
    private readonly IInputEnvironment _input;
    private readonly IVisionOverlay _overlay;
    private readonly ISoundCue _sound;
    private readonly ISaveFilePicker _savePicker;
    private readonly InputTestRunner _tests;
    private readonly ControllerBindingCapture _capture = new();
    private readonly TelemetryCoordinator _telemetry;
    private readonly CloudCoordinator _cloud;
    private bool _directSourceWarningShown;

    public DeckController(BotCore bot, SettingsSession session, IUiSink ui, IUiDispatcher dispatcher,
        IInputEnvironment input, IVisionOverlay overlay, ISoundCue sound, ISaveFilePicker savePicker,
        InputTestRunner tests, CloudCoordinator cloud = null)
    {
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _sound = sound ?? throw new ArgumentNullException(nameof(sound));
        _savePicker = savePicker ?? throw new ArgumentNullException(nameof(savePicker));
        _tests = tests ?? throw new ArgumentNullException(nameof(tests));
        _telemetry = new TelemetryCoordinator(bot, dispatcher, Toast, PostStatus);
        _cloud = cloud;
        _cloud?.Attach(Toast, PostStatus, OnCloudProfilesWritten);
    }

    public bool VisionOverlayVisible { get; private set; }

    public bool AnchorScanVisible { get; private set; } = true;

    public ControllerToggleAction CapturingBinding => _capture.Capturing;

    /// <summary>Loads the startup profile and calibrates to the primary screen.</summary>
    public void Initialize(int screenWidth, int screenHeight)
    {
        _session.Initialize(screenWidth.ToString(), screenHeight.ToString());
        _bot.UpdateResolution(screenWidth, screenHeight);
        _cloud?.Restore();
    }

    public void Handle(UiCommand command)
    {
        try
        {
            switch (command)
            {
                case ReadyCommand:
                    SendInit();
                    break;
                case SettingsCommand c:
                    _session.ReplaceDraft(SettingsCodec.ApplyJson(_session.Draft, c.Settings));
                    PostStatus();
                    SendSettings();
                    break;
                case StartCommand c:
                    OnStart(c.RequestId);
                    break;
                case ApplyCommand c:
                    OnApply(c.RequestId);
                    break;
                case TogglePauseCommand:
                    OnTogglePause();
                    break;
                case ResolutionCommand:
                    OnResolution();
                    break;
                case ScanCommand:
                    if (_session.TryReadResolution(out int width, out int height)) _bot.UpdateResolution(width, height);
                    SendDialog("Screen scan", _bot.DebugScan(), "DIAGNOSTICS");
                    break;
                case TestInputCommand:
                    OnTestInput();
                    break;
                case ProfileSaveCommand:
                    _session.SaveActive();
                    PostStatus();
                    Toast($"Profile saved: {_session.ActiveProfile}.", "success");
                    _cloud?.UploadProfile(_session.ActiveProfile);
                    break;
                case ProfileLoadCommand c:
                    OnProfileLoaded(_session.TryLoadActive(c.Discard, c.DraftDirty), statusOnError: true);
                    break;
                case ProfileSelectCommand c:
                    OnProfileLoaded(_session.Select(c.Name, c.Discard, c.DraftDirty), statusOnError: true);
                    break;
                case ProfileSaveAsCommand c:
                    OnProfileSaveAs(c.Name);
                    break;
                case ProfileDeleteCommand c:
                    OnProfileDelete(c.Name, c.Discard, c.DraftDirty);
                    break;
                case HelpCommand { Topic: HelpTopic.HowTo }:
                    SendDialog("How to use", HelpContent.HowTo, "SETUP GUIDE");
                    break;
                case HelpCommand { Topic: HelpTopic.ReadMe }:
                    SendDialog("Feature notes", HelpContent.ReadMe, "REFERENCE");
                    break;
                case OrangeParryToggleCommand:
                    ToggleOrangeParry();
                    break;
                case VisionOverlayToggleCommand:
                    ToggleVisionOverlay();
                    break;
                case AnchorScanToggleCommand:
                    ToggleAnchorScan();
                    break;
                case BindCaptureCommand c:
                    ToggleBindingCapture(c.Action);
                    break;
                case TelemetryToggleCommand c:
                    _telemetry.Toggle(c.Label);
                    break;
                case TelemetryExportCommand:
                    _telemetry.Export(_savePicker);
                    break;
                case CloudSignInCommand c:
                    WithCloud(cloud => cloud.SignIn(c.Email, c.Password));
                    break;
                case CloudSignUpCommand c:
                    WithCloud(cloud => cloud.SignUp(c.Email, c.Password));
                    break;
                case CloudSignOutCommand:
                    WithCloud(cloud => cloud.SignOut());
                    break;
                case CloudSyncCommand:
                    WithCloud(cloud => cloud.Sync());
                    break;
                case LicenseRedeemCommand c:
                    WithCloud(cloud => cloud.RedeemLicense(c.Key));
                    break;
            }
        }
        catch (Exception ex)
        {
            Toast("UI message failed: " + ex.Message, "error");
        }
    }

    /// <summary>The 200 ms tick: bridge recovery, hold gate, heartbeat, then status.</summary>
    public void OnStatusTick()
    {
        _input.TryRecover();
        _bot.FHeld = _input.HoldButtonHeld();

        if (!_bot.IsRunning || _bot.IsPaused)
            _bot.RecordBridgeHeartbeat();

        _cloud?.OnTick();
        // Pause rather than stop: a stopped BotCore cannot start again, and a
        // renewed license should only need Resume.
        if (_bot.IsRunning && !_bot.IsPaused && _cloud is { LicenseActive: false })
        {
            _bot.TogglePause();
            Toast("DANBOT paused: " + _cloud.LicenseBlockReason, "error");
        }

        InputBridgeSnapshot bridge = _input.GetDiagnostics();
        if (_ui.IsConnected && bridge.OtherXInputControllers > 0 && !_directSourceWarningShown)
        {
            _directSourceWarningShown = true;
            Toast("Direct DS4 mode detected another XInput controller. Close DS4Windows or other virtual-pad tools.", "warning");
        }

        PostStatus();
    }

    /// <summary>The 35 ms tick: bind capture and bound-button toggles.</summary>
    public void OnControllerPollTick()
    {
        bool hasState = _input.TryGetSourceState(out Native.XINPUT_GAMEPAD state);
        BindingEvent e = _capture.Poll(hasState, state);
        switch (e.Kind)
        {
            case BindingEventKind.Bound:
                _session.EditDraft(s => ControllerToggleBindings.Assign(s, e.Action, e.Button));
                SendSettings();
                Toast($"{ControllerToggleBindings.DisplayName(e.Action)} bound to {e.Button}.", "success");
                PostStatus();
                break;
            case BindingEventKind.Pressed:
                DispatchControllerToggle(ControllerToggleBindings.Resolve(_session.Draft, e.Button));
                break;
        }
    }

    public void PostStatus()
    {
        if (!_ui.IsConnected) return;
        _ui.Post(new StatusMessage(BuildStatus()));
    }

    public StatusDto BuildStatus() => StatusSnapshotBuilder.Build(_bot, _session, _input,
        new StatusSnapshotBuilder.UiFlags(_capture.Capturing, VisionOverlayVisible, AnchorScanVisible, _cloud?.Status));

    public void Dispose() => _tests.Dispose();

    private void SendInit() =>
        _ui.Post(new InitMessage(SettingsCodec.ToSnapshot(_session.Draft), BuildStatus()));

    private void SendSettings() => _ui.Post(new SettingsMessage(SettingsCodec.ToSnapshot(_session.Draft)));

    private void Toast(string message, string kind) => _ui.Post(new ToastMessage(message, kind));

    private void SendDialog(string title, string body, string eyebrow) => _ui.Post(new DialogMessage(title, body, eyebrow));

    private void ApplyResult(string requestId, bool success, string message) =>
        _ui.Post(new ApplyResultMessage(requestId, success, message));

    private void OnStart(string requestId)
    {
        if (_cloud is { LicenseActive: false })
        {
            string message = _cloud.LicenseBlockReason;
            ApplyResult(requestId, false, message);
            Toast(message, "error");
            return;
        }

        // The detection tuning only arrives with an active grant. Without it the
        // bot would run but see nothing, so block here with a clear message.
        if (_cloud is { TuningReady: false })
        {
            const string message = "DANBOT needs to finish activating. Connect to the internet once while signed in with an active license.";
            ApplyResult(requestId, false, message);
            Toast(message, "error");
            return;
        }

        if (!_input.IsReady)
        {
            const string message = "ViGEm input is unavailable. Reconnect the virtual controller before starting.";
            ApplyResult(requestId, false, message);
            Toast(message, "error");
            return;
        }

        if (!_session.TryReadResolution(out int width, out int height))
        {
            const string message = "Set a valid resolution before starting.";
            ApplyResult(requestId, false, message);
            Toast(message, "error");
            return;
        }

        _session.CommitAll();
        _bot.UpdateResolution(width, height);
        _sound.Beep();
        _bot.Start();
        ApplyResult(requestId, true, "Timings applied.");
        Toast("DANBOT is running.", "success");
        PostStatus();
    }

    private void OnApply(string requestId)
    {
        try
        {
            if (!_session.TryReadResolution(out int width, out int height))
            {
                const string invalidMessage = "Set a valid resolution before applying settings.";
                ApplyResult(requestId, false, invalidMessage);
                PostStatus();
                Toast(invalidMessage, "error");
                return;
            }
            _session.CommitAll();
            _bot.UpdateResolution(width, height);
            SendSettings();
            PostStatus();
            ApplyResult(requestId, true, "Timings applied.");
            Toast("Timings applied.", "success");
        }
        catch (Exception ex)
        {
            string failureMessage = "Timings were not applied: " + ex.Message;
            ApplyResult(requestId, false, failureMessage);
            PostStatus();
            Toast(failureMessage, "error");
        }
    }

    private void OnTogglePause()
    {
        if (!_bot.IsRunning) return;
        if (_bot.IsPaused && _cloud is { LicenseActive: false })
        {
            Toast(_cloud.LicenseBlockReason, "error");
            return;
        }
        _bot.TogglePause();
        Toast(_bot.IsPaused ? "DANBOT paused." : "DANBOT resumed.", "info");
        PostStatus();
    }

    private void OnResolution()
    {
        if (!_session.TryReadResolution(out int width, out int height))
        {
            Toast("Enter a valid game resolution first.", "error");
            return;
        }
        _bot.UpdateResolution(width, height);
        Toast($"Resolution set to {width} x {height}.", "success");
    }

    private void OnTestInput()
    {
        if (_bot.IsRunning && !_bot.IsPaused)
        {
            Toast("Pause the bot before testing input.", "error");
            return;
        }

        string name = _tests.StartNext(tested => _dispatcher.Post(() => Toast($"Tested {tested}.", "success")));
        Toast($"Testing {name} in 3 seconds.", "info");
    }

    private void OnProfileLoaded(string error, bool statusOnError)
    {
        if (error != null)
        {
            if (statusOnError) PostStatus();
            Toast(error, "error");
            return;
        }
        SendSettings();
        PostStatus();
        Toast($"Profile loaded: {_session.ActiveProfile}.", "success");
    }

    private void OnProfileSaveAs(string profileName)
    {
        string error = _session.SaveAs(profileName);
        if (error != null)
        {
            Toast(error, "error");
            return;
        }
        SendSettings();
        PostStatus();
        Toast($"Profile saved as: {_session.ActiveProfile}.", "success");
        _cloud?.UploadProfile(_session.ActiveProfile);
    }

    private void OnProfileDelete(string profileName, bool discard, bool draftDirty)
    {
        string error = _session.Delete(profileName, discard, draftDirty);
        if (error != null)
        {
            if (error == ProfileEditorController.DiscardBeforeDelete) PostStatus();
            Toast(error, "error");
            return;
        }
        SendSettings();
        PostStatus();
        // Validated inside Delete, so this cannot throw on the success path.
        string deleted = ProfileStore.NormalizeProfileName(profileName);
        Toast($"Profile deleted: {deleted}.", "success");
        _cloud?.DeleteProfile(deleted);
    }

    private void WithCloud(Action<CloudCoordinator> action)
    {
        if (_cloud == null)
        {
            Toast("Cloud profiles are not available in this build.", "error");
            return;
        }
        action(_cloud);
    }

    /// <summary>
    /// Sync wrote local profile files. Reload the active one only when the
    /// draft has no unsaved edits, so a download never discards work.
    /// </summary>
    private void OnCloudProfilesWritten(IReadOnlyCollection<string> names)
    {
        bool activeChanged = names.Contains(_session.ActiveProfile, StringComparer.OrdinalIgnoreCase);
        if (activeChanged && !_session.IsDirty && _session.TryLoadActive(discard: true, draftDirty: false) == null)
            SendSettings();
        else if (activeChanged)
            Toast($"A newer cloud copy of {_session.ActiveProfile} was saved. Reload the profile to use it.", "warning");
        PostStatus();
    }

    private void ToggleOrangeParry()
    {
        bool enabled = !_bot.OrangeParry;
        _session.EditDraft(s => s.OrangeParry = enabled);
        Toast(enabled ? "Orange parry ON" : "Orange parry OFF", enabled ? "success" : "info");
        SendSettings();
        PostStatus();
    }

    private void ToggleAutoDodge()
    {
        Settings next = _session.EditDraft(s => s.Unblockables = !s.Unblockables);
        SendSettings();
        Toast(next.Unblockables ? "Auto dodge ON." : "Auto dodge OFF.", next.Unblockables ? "success" : "info");
        PostStatus();
    }

    private void ToggleAutoParry()
    {
        Settings next = _session.EditDraft(s => s.Parry = !s.Parry);
        SendSettings();
        Toast(next.Parry ? "Auto parry ON." : "Auto parry OFF.", next.Parry ? "success" : "info");
        PostStatus();
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

    private void ToggleVisionOverlay()
    {
        if (VisionOverlayVisible)
        {
            _overlay.Hide();
            VisionOverlayVisible = false;
            Toast("Vision overlay hidden.", "info");
        }
        else if (_overlay.TryShow())
        {
            VisionOverlayVisible = true;
            Toast("Vision overlay enabled.", "success");
        }
        else
        {
            VisionOverlayVisible = false;
            Toast("Vision overlay could not be excluded from screen capture, so it stayed off.", "error");
        }
        PostStatus();
    }

    private void ToggleAnchorScan()
    {
        AnchorScanVisible = !AnchorScanVisible;
        _overlay.SetAnchorScanVisible(AnchorScanVisible);
        Toast(AnchorScanVisible ? "Anchor scan shown." : "Anchor scan hidden.", "info");
        PostStatus();
    }

    private void ToggleBindingCapture(ControllerToggleAction action)
    {
        string displayName = ControllerToggleBindings.DisplayName(action);
        if (_capture.Capturing == action)
        {
            _capture.Cancel();
            Toast($"{displayName} binding cancelled.", "info");
            PostStatus();
            return;
        }

        if (!_input.SourceConnected || !_input.TryGetSourceState(out Native.XINPUT_GAMEPAD source))
        {
            Toast($"Connect the physical source controller before binding {displayName.ToLowerInvariant()}.", "error");
            return;
        }

        _capture.Begin(action, source);
        Toast($"Press a controller button to bind {displayName.ToLowerInvariant()}.", "info");
        PostStatus();
    }
}
