using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HappyBot.Cloud;

/// <summary>
/// Keeps the refresh token in a file encrypted with Windows DPAPI for the
/// current user, so another account on the PC (or a copied file) cannot use it.
/// </summary>
internal sealed class DpapiCloudSessionStore : ICloudSessionStore
{
    private const int CryptProtectUiForbidden = 0x1;

    private readonly string _path;

    public DpapiCloudSessionStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public static DpapiCloudSessionStore ForApplication()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return new DpapiCloudSessionStore(Path.Combine(root, "DANBOT", "cloud-session.bin"));
    }

    public StoredCloudLogin Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            byte[] plain = Unprotect(File.ReadAllBytes(_path));
            StoredCloudLogin login = JsonSerializer.Deserialize<StoredCloudLogin>(Encoding.UTF8.GetString(plain));
            return string.IsNullOrEmpty(login?.RefreshToken) ? null : login;
        }
        catch
        {
            return null;
        }
    }

    public void Save(StoredCloudLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] encrypted = Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(login)));
        string temp = _path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, _path, true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // A stale file only means the next start tries a refresh that fails.
        }
    }

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true);

    private static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        GCHandle pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        var dataIn = new DataBlob { Size = input.Length, Data = pinned.AddrOfPinnedObject() };
        var dataOut = new DataBlob();
        try
        {
            bool ok = protect
                ? CryptProtectData(ref dataIn, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref dataOut);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            byte[] output = new byte[dataOut.Size];
            Marshal.Copy(dataOut.Data, output, 0, dataOut.Size);
            return output;
        }
        finally
        {
            pinned.Free();
            if (dataOut.Data != IntPtr.Zero) LocalFree(dataOut.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
