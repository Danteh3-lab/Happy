using HappyBot.App;

namespace HappyBot.Shell;

/// <summary>Production <see cref="IInputEnvironment"/> over the static Input/ViGEmInput state.</summary>
internal sealed class StaticInputEnvironment : IInputEnvironment
{
    public bool IsReady => Input.IsReady;
    public bool CanSendBulwark => Input.CanSendBulwark;
    public long InjectedCount => Interlocked.Read(ref Input.InjectedCount);
    public string ActiveMode => Input.ActiveMode;
    public bool SourceConnected => ViGEmInput.SourceConnected;
    public int SourceSlot => ViGEmInput.SourceSlot;
    public string SourceType => ViGEmInput.SourceType;
    public bool VirtualAvailable => ViGEmInput.IsAvailable;
    public bool HoldButtonHeld() => Input.HoldButtonHeld();
    public bool TryGetSourceState(out Native.XINPUT_GAMEPAD state) => ViGEmInput.TryGetSourceState(out state);
    public void TryRecover() => ViGEmInput.TryRecover();
    public InputBridgeSnapshot GetDiagnostics() => ViGEmInput.GetDiagnostics();
}
