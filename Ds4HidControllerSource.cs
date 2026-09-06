using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HappyBot;

/// <summary>
/// Reads a physical DualShock 4 HID stream without creating a second virtual
/// controller. The source publishes normalized XInput-compatible state to the
/// existing ViGEm merge layer.
/// </summary>
internal sealed class Ds4HidControllerSource : IControllerSource
{
    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const IntPtr InvalidHandleValue = -1;
    private const ushort SonyVendorId = 0x054C;
    private static readonly HashSet<ushort> KnownProductIds = new()
    {
        0x05C4, 0x09CC, 0x0BA0, 0x0CE6, 0x0DF2
    };

    private readonly Action<ControllerState, Ds4SourceDiagnostics> _stateChanged;
    private readonly object _streamSync = new();
    private readonly object _stateSync = new();
    private CancellationTokenSource _cts;
    private System.Threading.Timer _staleTimer;
    private Thread _thread;
    private FileStream _stream;
    private bool _disposed;
    private ControllerState _lastState;
    private bool _hasState;
    private long _reports;
    private long _stateChanges;
    private long _parseErrors;
    private long _readErrors;
    private long _reconnects;
    private long _lastReportTick;
    private long _lastReportIntervalMs;
    private long _maxReportIntervalMs;
    private bool _connected;
    private string _transport = "unknown";
    private string _fingerprint = "";

    public Ds4HidControllerSource(Action<ControllerState, Ds4SourceDiagnostics> stateChanged)
    {
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
    }

    public bool IsRunning => _thread is { IsAlive: true };

    public Ds4SourceDiagnostics Diagnostics
    {
        get
        {
            lock (_stateSync)
            {
                long lastAge = _lastReportTick == 0
                    ? -1
                    : Math.Max(0, Environment.TickCount64 - _lastReportTick);
                return new Ds4SourceDiagnostics(
                    _connected,
                    _transport,
                    _fingerprint,
                    _reports,
                    _stateChanges,
                    _parseErrors,
                    _readErrors,
                    _reconnects,
                    lastAge,
                    _lastReportIntervalMs,
                    _maxReportIntervalMs);
            }
        }
    }

    public void Start()
    {
        lock (_streamSync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Ds4HidControllerSource));
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _staleTimer = new System.Threading.Timer(_ => CheckStale(), null, 500, 250);
            _thread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "DANBOT DS4 HID source"
            };
            _thread.Start(_cts.Token);
        }
    }

    public void Stop()
    {
        Thread thread;
        CancellationTokenSource cts;
        System.Threading.Timer staleTimer;
        lock (_streamSync)
        {
            cts = _cts;
            thread = _thread;
            staleTimer = _staleTimer;
            _cts = null;
            _thread = null;
            _staleTimer = null;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }

        try { staleTimer?.Dispose(); } catch { }

        if (thread != null && thread != Thread.CurrentThread)
        {
            try { thread.Join(1000); } catch { }
        }
        try { cts?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (_streamSync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        GC.SuppressFinalize(this);
    }

    private void ReadLoop(object argument)
    {
        CancellationToken token = (CancellationToken)argument;
        bool wasConnected = false;
        while (!token.IsCancellationRequested)
        {
            FileStream stream = null;
            try
            {
                if (!TryOpen(out stream, out string transport, out string fingerprint,
                    out int inputReportLength))
                {
                    PublishDisconnectedIfNeeded(ref wasConnected);
                    token.WaitHandle.WaitOne(250);
                    continue;
                }

                _transport = transport;
                _fingerprint = fingerprint;
                lock (_stateSync) _connected = true;
                if (wasConnected) Interlocked.Increment(ref _reconnects);
                wasConnected = true;
                lock (_streamSync) _stream = stream;

                // HIDClass expects ReadFile buffers to match the report length
                // advertised by this interface. A fixed 78-byte Bluetooth
                // buffer can leave a 64-byte USB DS4 read pending forever,
                // which made the UI remain at Source OFF even though the
                // controller handle opened successfully.
                byte[] buffer = new byte[inputReportLength];
                while (!token.IsCancellationRequested)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    Interlocked.Increment(ref _reports);
                    if (!Ds4ReportParser.TryParse(buffer.AsSpan(0, read), out ControllerState state,
                        out string reportTransport))
                    {
                        Interlocked.Increment(ref _parseErrors);
                        continue;
                    }

                    _transport = reportTransport;
                    PublishState(state);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                Interlocked.Increment(ref _readErrors);
            }
            catch (Win32Exception)
            {
                Interlocked.Increment(ref _readErrors);
            }
            catch
            {
                Interlocked.Increment(ref _readErrors);
            }
            finally
            {
                lock (_streamSync)
                {
                    if (ReferenceEquals(_stream, stream)) _stream = null;
                }
                try { stream?.Dispose(); } catch { }
                PublishDisconnectedIfNeeded(ref wasConnected);
            }

            if (!token.IsCancellationRequested) token.WaitHandle.WaitOne(100);
        }

        PublishDisconnectedIfNeeded(ref wasConnected);
    }

    private void PublishState(ControllerState state)
    {
        Ds4SourceDiagnostics diagnostics;
        lock (_stateSync)
        {
            long now = Environment.TickCount64;
            if (_lastReportTick != 0)
            {
                long interval = Math.Max(0, now - _lastReportTick);
                _lastReportIntervalMs = interval;
                if (interval > _maxReportIntervalMs) _maxReportIntervalMs = interval;
            }
            _lastReportTick = now;
            if (!_hasState || state != _lastState) Interlocked.Increment(ref _stateChanges);
            _lastState = state;
            _hasState = true;
            _connected = true;
            diagnostics = Diagnostics;
        }
        _stateChanged(state, diagnostics with { Connected = true });
    }

    private void CheckStale()
    {
        Ds4SourceDiagnostics diagnostics;
        lock (_stateSync)
        {
            if (!_connected || _lastReportTick == 0 ||
                Environment.TickCount64 - _lastReportTick <= 500)
                return;
            _connected = false;
            diagnostics = Diagnostics;
        }
        _stateChanged(ControllerState.Empty, diagnostics with { Connected = false });
    }

    private void PublishDisconnectedIfNeeded(ref bool wasConnected)
    {
        if (!wasConnected) return;
        wasConnected = false;
        Ds4SourceDiagnostics diagnostics;
        lock (_stateSync)
        {
            _connected = false;
            diagnostics = Diagnostics;
        }
        _stateChanged(ControllerState.Empty, diagnostics with { Connected = false, Transport = _transport });
    }

    private static bool TryOpen(out FileStream stream, out string transport, out string fingerprint,
        out int inputReportLength)
    {
        stream = null;
        transport = "unknown";
        fingerprint = "";
        inputReportLength = 0;
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfo == InvalidHandleValue) return false;

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    cbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!SetupDiEnumDeviceInterfaces(deviceInfo, IntPtr.Zero, ref hidGuid, index,
                    ref interfaceData))
                {
                    break;
                }

                int required = 0;
                SetupDiGetDeviceInterfaceDetail(deviceInfo, ref interfaceData, IntPtr.Zero, 0,
                    ref required, IntPtr.Zero);
                if (required <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(deviceInfo, ref interfaceData, detail,
                        required, ref required, IntPtr.Zero)) continue;
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W has a platform-sized
                    // cbSize (8 on x64), but DevicePath itself begins directly
                    // after the 4-byte DWORD. Using offset 8 on x64 skipped the
                    // first two UTF-16 characters ("\\?"), producing a path
                    // that CreateFile could never open.
                    string path = Marshal.PtrToStringUni(detail + sizeof(int));
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    SafeFileHandle handle = CreateFile(path, GenericRead,
                        FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
                        FileAttributeNormal, IntPtr.Zero);
                    if (handle == null || handle.IsInvalid)
                    {
                        handle?.Dispose();
                        continue;
                    }

                    var attributes = new HidpAttributes
                    {
                        Size = Marshal.SizeOf<HidpAttributes>()
                    };
                    if (!HidD_GetAttributes(handle, ref attributes) ||
                        attributes.VendorID != SonyVendorId ||
                        !KnownProductIds.Contains(attributes.ProductID))
                    {
                        handle.Dispose();
                        continue;
                    }

                    if (!TryGetGamepadInputReportLength(handle, out inputReportLength))
                    {
                        handle.Dispose();
                        continue;
                    }

                    stream = new FileStream(handle, FileAccess.Read, 128, isAsync: false);
                    transport = path.Contains("BTH", StringComparison.OrdinalIgnoreCase)
                        ? "bluetooth" : "usb";
                    fingerprint = $"054C:{attributes.ProductID:X4}:{transport}";
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfo);
        }
        return false;
    }

    private static bool TryGetGamepadInputReportLength(SafeFileHandle handle,
        out int inputReportLength)
    {
        inputReportLength = 0;
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsedData) || preparsedData == IntPtr.Zero)
            return false;
        try
        {
            var caps = new HidpCaps
            {
                Reserved = new ushort[17]
            };
            // HIDP_STATUS_SUCCESS is 0x00110000.
            if (HidP_GetCaps(preparsedData, ref caps) != 0x00110000) return false;
            if (caps.UsagePage != 0x01 || (caps.Usage != 0x04 && caps.Usage != 0x05))
                return false;

            inputReportLength = caps.InputReportByteLength;
            return inputReportLength >= 9 && inputReportLength <= 4096;
        }
        finally
        {
            try { HidD_FreePreparsedData(preparsedData); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject,
        ref HidpAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject,
        out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet,
        IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);
}

internal readonly record struct Ds4SourceDiagnostics(
    bool Connected,
    string Transport,
    string Fingerprint,
    long Reports,
    long StateChanges,
    long ParseErrors,
    long ReadErrors,
    long Reconnects,
    long LastReportAgeMs,
    long LastReportIntervalMs,
    long MaxReportIntervalMs);
