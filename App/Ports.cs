using HappyBot.App.Protocol;

namespace HappyBot.App;

/// <summary>Delivers messages to the page. Implementations marshal to the UI thread themselves.</summary>
internal interface IUiSink
{
    /// <summary>True once the page has loaded and can receive messages.</summary>
    bool IsConnected { get; }

    void Post(UiMessage message);
}

/// <summary>Runs work on the UI thread; used by background completions.</summary>
internal interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>The static input and controller-bridge state, behind a seam for tests.</summary>
internal interface IInputEnvironment
{
    bool IsReady { get; }
    bool CanSendBulwark { get; }
    long InjectedCount { get; }
    string ActiveMode { get; }
    bool SourceConnected { get; }
    int SourceSlot { get; }
    string SourceType { get; }
    bool VirtualAvailable { get; }
    bool HoldButtonHeld();
    bool TryGetSourceState(out Native.XINPUT_GAMEPAD state);
    void TryRecover();
    InputBridgeSnapshot GetDiagnostics();
}

internal interface IVisionOverlay
{
    bool TryShow();
    void Hide();
    void SetAnchorScanVisible(bool visible);
}

internal interface ISoundCue
{
    void Click();
    void Unclick();
    void Beep();
}

internal interface ISaveFilePicker
{
    /// <summary>Returns the chosen path, or null when the user cancels.</summary>
    string PickSavePath(string title, string filter, string suggestedName);
}
