using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HappyBot;

public sealed class ProfileStore
{
    public const string DefaultProfileName = "Default";
    private const string ApplicationDataFolderName = "DANBOT";
    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9 _-]{0,47}$", RegexOptions.CultureInvariant);
    private readonly string _rootDirectory;
    private readonly string _legacyPath;
    private readonly string _profilesDirectory;
    private readonly string _deletedProfilesDirectory;
    private readonly string _activeProfilePath;
    private readonly string _fallbackLegacyPath;
    private readonly string _fallbackProfilesDirectory;

    public ProfileStore(string baseDirectory)
        : this(baseDirectory, "")
    {
    }

    public ProfileStore(string baseDirectory, string legacyBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) throw new ArgumentException("A base directory is required.", nameof(baseDirectory));
        _rootDirectory = System.IO.Path.GetFullPath(baseDirectory);
        _legacyPath = System.IO.Path.Combine(_rootDirectory, "Config.ini");
        _profilesDirectory = System.IO.Path.Combine(_rootDirectory, "Profiles");
        _deletedProfilesDirectory = System.IO.Path.Combine(_rootDirectory, "DeletedProfiles");
        _activeProfilePath = System.IO.Path.Combine(_rootDirectory, "ActiveProfile.txt");
        string fallbackRoot = string.IsNullOrWhiteSpace(legacyBaseDirectory)
            ? ""
            : System.IO.Path.GetFullPath(legacyBaseDirectory);
        if (string.Equals(_rootDirectory, fallbackRoot, StringComparison.OrdinalIgnoreCase)) fallbackRoot = "";
        _fallbackLegacyPath = fallbackRoot.Length == 0 ? "" : System.IO.Path.Combine(fallbackRoot, "Config.ini");
        _fallbackProfilesDirectory = fallbackRoot.Length == 0 ? "" : System.IO.Path.Combine(fallbackRoot, "Profiles");
    }

    public static ProfileStore ForApplication()
    {
        string stableRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(stableRoot)) stableRoot = AppContext.BaseDirectory;
        return new ProfileStore(Path.Combine(stableRoot, ApplicationDataFolderName), AppContext.BaseDirectory);
    }

    public IReadOnlyList<string> ListProfiles()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultProfileName };
        AddProfileNames(_profilesDirectory, names);
        AddProfileNames(_fallbackProfilesDirectory, names);

        names.RemoveWhere(name => !IsDefault(name) && IsDeleted(name));
        return names.OrderBy(name => name.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddProfileNames(string directory, HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        foreach (string file in Directory.EnumerateFiles(directory, "*.ini", SearchOption.TopDirectoryOnly))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(file);
            if (TryNormalizeProfileName(name, out string normalized) && !IsDefault(normalized)) names.Add(normalized);
        }
    }

    public string Read(string profileName, string key)
    {
        string normalized = NormalizeProfileName(profileName);
        if (IsDeleted(normalized)) return "";
        string path = ResolveReadPath(normalized);
        var sb = new StringBuilder(512);
        GetPrivateProfileString("Options", key, "", sb, sb.Capacity, path);
        return sb.ToString();
    }

    public void Write(string profileName, string key, string value)
    {
        WriteAll(profileName, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value ?? "" });
    }

    public void WriteAll(string profileName, IReadOnlyDictionary<string, string> values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        string normalized = NormalizeProfileName(profileName);
        string path = GetProfilePath(normalized);
        Directory.CreateDirectory(_rootDirectory);
        if (!IsDefault(normalized)) Directory.CreateDirectory(_profilesDirectory);

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string source = ResolveReadPath(normalized);
            if (File.Exists(source)) File.Copy(source, tempPath, true);

            foreach ((string key, string value) in values)
            {
                if (!WritePrivateProfileString("Options", key, value ?? "", tempPath))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write the profile.");
            }

            // Flush the Win32 profile cache before the temp file becomes visible.
            _ = WritePrivateProfileString(null, null, null, tempPath);

            ReplaceAtomically(tempPath, path);
            ClearDeletionTombstone(normalized);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }

    public void Delete(string profileName)
    {
        string normalized = NormalizeProfileName(profileName);
        if (IsDefault(normalized)) throw new InvalidOperationException("The Default profile cannot be deleted.");
        WriteDeletionTombstone(normalized);
        string path = GetProfilePath(normalized);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            ClearDeletionTombstone(normalized);
            throw;
        }
        string fallbackPath = GetFallbackProfilePath(normalized);
        if (fallbackPath.Length > 0 && File.Exists(fallbackPath))
        {
            try { File.Delete(fallbackPath); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    public string ReadActiveProfile()
    {
        try
        {
            string value = File.Exists(_activeProfilePath) ? File.ReadAllText(_activeProfilePath).Trim() : "";
            return TryNormalizeProfileName(value, out string normalized) ? normalized : "";
        }
        catch
        {
            return "";
        }
    }

    public void WriteActiveProfile(string profileName)
    {
        string normalized = NormalizeProfileName(profileName);
        Directory.CreateDirectory(_rootDirectory);
        string tempPath = _activeProfilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, normalized, new UTF8Encoding(false));
            ReplaceAtomically(tempPath, _activeProfilePath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }

    public string GetProfilePath(string profileName)
    {
        string normalized = NormalizeProfileName(profileName);
        return IsDefault(normalized)
            ? _legacyPath
            : System.IO.Path.Combine(_profilesDirectory, normalized + ".ini");
    }

    private string ResolveReadPath(string normalized)
    {
        if (IsDeleted(normalized)) return "";
        string path = GetProfilePath(normalized);
        if (File.Exists(path)) return path;
        string fallbackPath = GetFallbackProfilePath(normalized);
        return fallbackPath.Length > 0 && File.Exists(fallbackPath) ? fallbackPath : path;
    }

    private string GetFallbackProfilePath(string normalized)
    {
        if (_fallbackLegacyPath.Length == 0) return "";
        return IsDefault(normalized)
            ? _fallbackLegacyPath
            : Path.Combine(_fallbackProfilesDirectory, normalized + ".ini");
    }

    private string GetDeletionTombstonePath(string normalized) =>
        Path.Combine(_deletedProfilesDirectory, normalized + ".deleted");

    private bool IsDeleted(string normalized) =>
        !IsDefault(normalized) && File.Exists(GetDeletionTombstonePath(normalized));

    private void WriteDeletionTombstone(string normalized)
    {
        Directory.CreateDirectory(_deletedProfilesDirectory);
        string tombstonePath = GetDeletionTombstonePath(normalized);
        string tempPath = tombstonePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, normalized, new UTF8Encoding(false));
            ReplaceAtomically(tempPath, tombstonePath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }

    private void ClearDeletionTombstone(string normalized)
    {
        if (IsDefault(normalized)) return;
        string tombstonePath = GetDeletionTombstonePath(normalized);
        if (File.Exists(tombstonePath)) File.Delete(tombstonePath);
    }

    private static void ReplaceAtomically(string tempPath, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            File.Move(tempPath, targetPath);
            return;
        }

        try
        {
            File.Replace(tempPath, targetPath, null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tempPath, targetPath, true);
        }
        catch (IOException)
        {
            File.Move(tempPath, targetPath, true);
        }
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
    private static readonly ProfileStore Store = ProfileStore.ForApplication();

    public static string Read(string key) => Store.Read(ProfileStore.DefaultProfileName, key);

    public static void Write(string key, string value) => Store.Write(ProfileStore.DefaultProfileName, key, value);
}
