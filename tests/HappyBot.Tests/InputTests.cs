using System.Drawing;
using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;
using HappyBot.Infrastructure.Input;
using HappyBot.Vision;

static partial class Program
{
    private static void Ds4UsbReportMapsToXboxState()
    {
        byte[] report = new byte[Ds4ReportParser.UsbReportLength];
        report[0] = Ds4ReportParser.UsbReportId;
        report[1] = 0;   // left X
        report[2] = 255; // left Y down
        report[3] = 255; // right X
        report[4] = 0;   // right Y up
        report[5] = 0x60; // Cross + Circle
        report[6] = 0x03; // L1 + R1
        report[7] = 0; // PS/touchpad/counter byte
        report[8] = 31; // L2
        report[9] = 220; // R2
        ControllerState state = RequireDs4Report(report, "usb");
        Require((state.Buttons & 0x1000) != 0 && (state.Buttons & 0x2000) != 0,
            "USB face buttons should map to Xbox A/B");
        Require((state.Buttons & 0x0100) != 0 && (state.Buttons & 0x0200) != 0,
            "USB shoulders should map to Xbox LB/RB");
        Require(state.LeftTrigger == 31 && state.RightTrigger == 220 &&
            state.LeftX < -32000 && state.LeftY < -32000 &&
            state.RightX > 32000 && state.RightY > 32000,
            "USB sticks and triggers should preserve direction and range");
    }

    private static void Ds4BluetoothReportMapsToXboxState()
    {
        byte[] report = new byte[Ds4ReportParser.BluetoothReportLength];
        report[0] = Ds4ReportParser.BluetoothReportId;
        report[1] = 0xC0; // Bluetooth header
        report[2] = 0x00;
        report[3] = 128;
        report[4] = 128;
        report[5] = 128;
        report[6] = 128;
        report[7] = 0x12; // d-pad right + square
        report[8] = 0x10; // share
        report[9] = 0; // PS/touchpad/counter byte
        report[10] = 100;
        report[11] = 200;
        ControllerState state = RequireDs4Report(report, "bluetooth");
        Require((state.Buttons & 0x0008) != 0 && (state.Buttons & 0x4000) != 0 &&
            (state.Buttons & 0x0020) != 0 && state.LeftTrigger == 100 && state.RightTrigger == 200,
            "Bluetooth report should map its header, d-pad, face button, and triggers");
    }

    private static void Ds4MalformedReportIsRejected()
    {
        Require(!Ds4ReportParser.TryParse(new byte[] { Ds4ReportParser.UsbReportId, 1, 2 }, out _),
            "short DS4 reports must be rejected");
        Require(!Ds4ReportParser.TryParse(new byte[64], out _),
            "unknown DS4 report IDs must be rejected");
        byte[] minimal = new byte[10];
        minimal[0] = Ds4ReportParser.UsbReportId;
        minimal[1] = 128;
        minimal[2] = 128;
        minimal[3] = 128;
        minimal[4] = 128;
        Require(Ds4ReportParser.TryParse(minimal, out _),
            "the documented minimal DS4 report should be accepted");
    }

    private static ControllerState RequireDs4Report(byte[] report, string expectedTransport)
    {
        Require(Ds4ReportParser.TryParse(report, out ControllerState state, out string transport) &&
            transport == expectedTransport, expectedTransport + " report should parse");
        return state;
    }

    private static Native.XINPUT_GAMEPAD Pad(ushort buttons = 0, byte lt = 0, byte rt = 0,
        short lx = 0, short ly = 0, short rx = 0, short ry = 0) => new()
        {
            wButtons = buttons,
            bLeftTrigger = lt,
            bRightTrigger = rt,
            sThumbLX = lx,
            sThumbLY = ly,
            sThumbRX = rx,
            sThumbRY = ry
        };

    private static void MergerPrefersBotSticksOtherwiseSource()
    {
        var source = Pad(lx: 1000, ly: 2000, rx: 3000, ry: 4000);
        MergedControllerReport passthrough = ControllerReportMerger.Merge(source, Pad(), false, 0, 0);
        Require(passthrough.LeftX == 1000 && passthrough.LeftY == 2000 &&
            passthrough.RightX == 3000 && passthrough.RightY == 4000,
            "idle automation must pass the source sticks through");

        MergedControllerReport botWins = ControllerReportMerger.Merge(source, Pad(lx: -5000, ly: -6000), false, 0, 0);
        Require(botWins.LeftX == -5000 && botWins.LeftY == -6000,
            "nonzero automation sticks must win over the source");
        Require(botWins.RightX == 3000 && botWins.RightY == 4000,
            "the right stick must still follow the source without automation input");
    }

    private static void MergerOrButtonsMaxTriggersAndOverride()
    {
        MergedControllerReport merged = ControllerReportMerger.Merge(
            Pad(buttons: 0x1000, rt: 100), Pad(buttons: 0x0200, rt: 200), false, 0, 0);
        Require(merged.Buttons == 0x1200, "buttons must OR");
        Require(merged.RightTrigger == 200 && merged.LeftTrigger == 0, "triggers must take the maximum");

        MergedControllerReport over = ControllerReportMerger.Merge(
            Pad(), Pad(rx: 1111, ry: 2222), true, 7777, 8888);
        Require(over.RightX == 7777 && over.RightY == 8888,
            "the temporary stick override must beat the automation guard");
    }

    private static void MergerReportsCompareByValue()
    {
        MergedControllerReport a = ControllerReportMerger.Merge(Pad(lx: 5), Pad(), false, 0, 0);
        MergedControllerReport b = ControllerReportMerger.Merge(Pad(lx: 5), Pad(), false, 0, 0);
        Require(a == b, "identical inputs must dedupe to one report");
        Require(a != ControllerReportMerger.Merge(Pad(lx: 6), Pad(), false, 0, 0),
            "changed inputs must produce a new report");
    }
}
