namespace HappyBot;

/// <summary>
/// Parses the gameplay portion of DualShock 4 USB and Bluetooth input
/// reports. This class is deliberately pure so saved reports can be replayed
/// in tests without opening a controller handle.
/// </summary>
public static class Ds4ReportParser
{
    public const byte UsbReportId = 0x01;
    public const byte BluetoothReportId = 0x11;
    public const int UsbReportLength = 64;
    public const int BluetoothReportLength = 78;

    // DS4 common input data starts immediately after the report ID for USB.
    // Bluetooth reports have two reserved bytes between the report ID and the
    // common data block.
    private const int UsbCommonOffset = 1;
    private const int BluetoothCommonOffset = 3;

    public static bool TryParse(ReadOnlySpan<byte> report, out ControllerState state,
        out string transport)
    {
        state = ControllerState.Empty;
        transport = "unknown";
        if (report.Length == 0) return false;

        int commonOffset;
        switch (report[0])
        {
            case UsbReportId:
                commonOffset = UsbCommonOffset;
                transport = "usb";
                break;
            case BluetoothReportId:
                commonOffset = BluetoothCommonOffset;
                transport = "bluetooth";
                break;
            default:
                return false;
        }

        // Full USB/Bluetooth reports contain a 32-byte common block. A few
        // Bluetooth stacks emit the documented minimal 0x01 report instead;
        // it contains the same first nine gameplay bytes but no sensors.
        bool minimalReport = report[0] == UsbReportId && report.Length < commonOffset + 32;
        if (report.Length < commonOffset + (minimalReport ? 9 : 32)) return false;

        byte leftX = report[commonOffset];
        byte leftY = report[commonOffset + 1];
        byte rightX = report[commonOffset + 2];
        byte rightY = report[commonOffset + 3];
        byte buttons0 = report[commonOffset + 4];
        byte buttons1 = report[commonOffset + 5];
        // common[6] is the third DS4 button byte (PS/touchpad/counter),
        // followed by the two analog trigger values.
        byte leftTrigger = report[commonOffset + 7];
        byte rightTrigger = report[commonOffset + 8];

        ushort buttons = FaceAndShoulderButtons(buttons0, buttons1);
        buttons |= DPadButtons((byte)(buttons0 & 0x0F));

        state = new ControllerState(
            buttons,
            leftTrigger,
            rightTrigger,
            Stick(leftX, invert: false),
            Stick(leftY, invert: true),
            Stick(rightX, invert: false),
            Stick(rightY, invert: true));
        return true;
    }

    public static bool TryParse(ReadOnlySpan<byte> report, out ControllerState state) =>
        TryParse(report, out state, out _);

    private static ushort FaceAndShoulderButtons(byte buttons0, byte buttons1)
    {
        ushort result = 0;

        // DS4 face bits: Square, Cross, Circle, Triangle. Xbox order is X, A,
        // B, Y respectively.
        if ((buttons0 & 0x10) != 0) result |= 0x4000; // Square -> X
        if ((buttons0 & 0x20) != 0) result |= 0x1000; // Cross -> A
        if ((buttons0 & 0x40) != 0) result |= 0x2000; // Circle -> B
        if ((buttons0 & 0x80) != 0) result |= 0x8000; // Triangle -> Y

        if ((buttons1 & 0x01) != 0) result |= 0x0100; // L1 -> LB
        if ((buttons1 & 0x02) != 0) result |= 0x0200; // R1 -> RB
        if ((buttons1 & 0x10) != 0) result |= 0x0020; // Share -> Back
        if ((buttons1 & 0x20) != 0) result |= 0x0010; // Options -> Start
        if ((buttons1 & 0x40) != 0) result |= 0x0040; // L3
        if ((buttons1 & 0x80) != 0) result |= 0x0080; // R3
        return result;
    }

    private static ushort DPadButtons(byte hat)
    {
        ushort result = 0;
        bool up = hat is 0 or 1 or 7;
        bool right = hat is 1 or 2 or 3;
        bool down = hat is 3 or 4 or 5;
        bool left = hat is 5 or 6 or 7;
        if (up) result |= 0x0001;
        if (down) result |= 0x0002;
        if (left) result |= 0x0004;
        if (right) result |= 0x0008;
        return result;
    }

    private static short Stick(byte value, bool invert)
    {
        int signed = (value - 128) * 256;
        if (invert) signed = -signed;
        return (short)Math.Clamp(signed, short.MinValue, short.MaxValue);
    }
}
