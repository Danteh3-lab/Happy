using HappyBot;

namespace HappyBot.Infrastructure.Input;

/// <summary>
/// Narrow input seam used by combat automation.  The production adapter keeps
/// the existing Input/ViGEmInput mappings and modes unchanged; tests can use a
/// fake gateway without invoking Win32 or a virtual controller.
/// </summary>
internal interface IInputGateway
{
    bool IsReady { get; }
    bool UsesControllerBridge { get; }
    bool CanSendBulwark { get; }
    bool IsDown(int virtualKey);
    bool HoldButtonHeld();
    bool PhysicalHeavyAttackHeld();
    bool PhysicalLightAttackHeld();
    bool MovingForwardHeld();
    bool KeyDown(int virtualKey);
    bool KeyUp(int virtualKey);
    bool KeyTap(int virtualKey);
    bool MouseClick(int virtualKey);
    void Block(bool on);
    bool BeginBulwarkStance();
    void EndBulwarkStance();
    bool DirectionalLight(int guardKey);
    void ReleaseAutomationInputs();
    InputBridgeSnapshot Diagnostics { get; }
}

/// <summary>Adapter over the legacy static Input and ViGEmInput APIs.</summary>
internal sealed class StaticInputGateway : IInputGateway
{
    public bool IsReady => HappyBot.Input.IsReady;
    public bool UsesControllerBridge => HappyBot.Input.UsesControllerBridge;
    public bool CanSendBulwark => HappyBot.Input.CanSendBulwark;
    public bool IsDown(int virtualKey) => HappyBot.Input.IsDown(virtualKey);
    public bool HoldButtonHeld() => HappyBot.Input.HoldButtonHeld();
    public bool PhysicalHeavyAttackHeld() => HappyBot.Input.PhysicalHeavyAttackHeld();
    public bool PhysicalLightAttackHeld() => HappyBot.Input.PhysicalLightAttackHeld();
    public bool MovingForwardHeld() => HappyBot.Input.MovingForwardHeld();
    public bool KeyDown(int virtualKey) => HappyBot.Input.KeyDown(virtualKey);
    public bool KeyUp(int virtualKey) => HappyBot.Input.KeyUp(virtualKey);
    public bool KeyTap(int virtualKey) => HappyBot.Input.KeyTap(virtualKey);
    public bool MouseClick(int virtualKey) => HappyBot.Input.MouseClick(virtualKey);
    public void Block(bool on) => HappyBot.Input.Block(on);
    public bool BeginBulwarkStance() => HappyBot.Input.BeginBulwarkStance();
    public void EndBulwarkStance() => HappyBot.Input.EndBulwarkStance();
    public bool DirectionalLight(int guardKey) => HappyBot.Input.DirectionalLight(guardKey);
    public void ReleaseAutomationInputs() => HappyBot.Input.ReleaseAutomationInputs();
    public InputBridgeSnapshot Diagnostics => ViGEmInput.GetDiagnostics();
}
