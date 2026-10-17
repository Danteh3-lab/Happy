using System.Runtime.InteropServices;
using System.Text;

namespace HappyBot.Cloud;

/// <summary>
/// A small text file encrypted with Windows DPAPI for the current user, so
/// another account on the PC (or a copied file) cannot read it.
/// </summary>
internal sealed class DpapiFile
{
    private const int CryptProtectUiForbidden = 0x1;

    public DpapiFile(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Path { get; }

    /// <summary>A file in %LOCALAPPDATA%\DANBOT (or next to the exe when that is unavailable).</summary>
    public static DpapiFile InAppData(string fileName)
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return new DpapiFile(System.IO.Path.Combine(root, "DANBOT", fileName));
    }

    /// <summary>Returns null when the file is missing or cannot be decrypted.</summary>
    public string Read()
    {
        try
        {
            return File.Exists(Path) ? Encoding.UTF8.GetString(Transform(File.ReadAllBytes(Path), protect: false)) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        byte[] encrypted = Transform(Encoding.UTF8.GetBytes(text), protect: true);
        string temp = Path + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, Path, true);
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch
        {
            // A stale file is ignored or replaced on the next write.
        }
    }

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
