using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HappyBot;

public sealed class ProfileStore
{
    public const string DefaultProfileName = "Default";
    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9 _-]{0,47}$", RegexOptions.CultureInvariant);
    private readonly string _legacyPath;
    private readonly string _profilesDirectory;

    public ProfileStore(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) throw new ArgumentException("A base directory is required.", nameof(baseDirectory));
        string root = System.IO.Path.GetFullPath(baseDirectory);
        _legacyPath = System.IO.Path.Combine(root, "Config.ini");
        _profilesDirectory = System.IO.Path.Combine(root, "Profiles");
    }

    public IReadOnlyList<string> ListProfiles()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultProfileName };
        if (Directory.Exists(_profilesDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_profilesDirectory, "*.ini", SearchOption.TopDirectoryOnly))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (TryNormalizeProfileName(name, out string normalized) && !IsDefault(normalized)) names.Add(normalized);
            }
        }

        return names.OrderBy(name => name.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string Read(string profileName, string key)
    {
        string path = GetProfilePath(profileName);
        var sb = new StringBuilder(512);
        GetPrivateProfileString("Options", key, "", sb, sb.Capacity, path);
        return sb.ToString();
    }

    public void Write(string profileName, string key, string value)
    {
        string path = GetProfilePath(profileName);
        if (!IsDefault(profileName)) Directory.CreateDirectory(_profilesDirectory);
        if (!WritePrivateProfileString("Options", key, value ?? "", path))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write the profile.");
    }

    public void Delete(string profileName)
    {
        string normalized = NormalizeProfileName(profileName);
        if (IsDefault(normalized)) throw new InvalidOperationException("The Default profile cannot be deleted.");
        string path = GetProfilePath(normalized);
        if (File.Exists(path)) File.Delete(path);
    }

    public string GetProfilePath(string profileName)
    {
        string normalized = NormalizeProfileName(profileName);
        return IsDefault(normalized)
            ? _legacyPath
            : System.IO.Path.Combine(_profilesDirectory, normalized + ".ini");
    }

    public static string NormalizeProfileName(string profileName)
    {
        if (!TryNormalizeProfileName(profileName, out string normalized))
            throw new ArgumentException("Profile names may contain letters, numbers, spaces, hyphens, and underscores only.", nameof(profileName));
        return normalized;
    }

    private static bool TryNormalizeProfileName(string profileName, out string normalized)
    {
        normalized = (profileName ?? "").Trim();
        if (normalized.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase)) normalized = DefaultProfileName;
        return normalized.Length > 0 && normalized != "." && normalized != ".." && SafeName.IsMatch(normalized);
    }

    private static bool IsDefault(string profileName) => profileName.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPrivateProfileString(string app, string key, string def, StringBuilder ret, int size, string file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WritePrivateProfileString(string app, string key, string val, string file);
}

public static class Config
{
    private static readonly ProfileStore Store = new(AppContext.BaseDirectory);

    public static string Read(string key) => Store.Read(ProfileStore.DefaultProfileName, key);

    public static void Write(string key, string value) => Store.Write(ProfileStore.DefaultProfileName, key, value);
}
