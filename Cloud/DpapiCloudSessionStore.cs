using System.Text.Json;

namespace HappyBot.Cloud;

/// <summary>
/// Keeps the refresh token in a file encrypted with Windows DPAPI for the
/// current user, so another account on the PC (or a copied file) cannot use it.
/// </summary>
internal sealed class DpapiCloudSessionStore : ICloudSessionStore
{
    private readonly DpapiFile _file;

    public DpapiCloudSessionStore(string path)
    {
        _file = new DpapiFile(path);
    }

    public static DpapiCloudSessionStore ForApplication() => new(DpapiFile.InAppData("cloud-session.bin").Path);

    public StoredCloudLogin Load()
    {
        try
        {
            string json = _file.Read();
            if (json == null) return null;
            StoredCloudLogin login = JsonSerializer.Deserialize<StoredCloudLogin>(json);
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
        _file.Write(JsonSerializer.Serialize(login));
    }

    // A stale file only means the next start tries a refresh that fails.
    public void Clear() => _file.Delete();
}

/// <summary>The last signed license token, DPAPI-encrypted like the login.</summary>
internal sealed class DpapiLicenseTokenStore : ILicenseTokenStore
{
    private readonly DpapiFile _file;

    public DpapiLicenseTokenStore(string path)
    {
        _file = new DpapiFile(path);
    }

    public static DpapiLicenseTokenStore ForApplication() => new(DpapiFile.InAppData("license.bin").Path);

    public string Load() => _file.Read();

    public void Save(string token) => _file.Write(token ?? throw new ArgumentNullException(nameof(token)));

    public void Clear() => _file.Delete();
}
