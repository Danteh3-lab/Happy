using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HappyBot.Cloud;

/// <summary>
/// What the license service signed for one account on one PC. Only an
/// <see cref="Active"/> grant lets the bot start, and only until <see cref="ValidUntil"/>
/// (at most 72 hours after it was issued, never past the license expiry).
/// </summary>
internal sealed record LicenseGrant(
    string Status,
    string UserId,
    string DeviceId,
    string LicenseId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ValidUntil,
    DateTimeOffset? ExpiresAt,
    int MaxDevices,
    int DevicesUsed,
    string KeyHint)
{
    public const string Active = "active";
    public const string None = "none";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
    public const string DeviceLimit = "device_limit";

    public bool IsActive => Status == Active;
}

internal interface ILicenseTokenStore
{
    /// <summary>Returns null when nothing is stored or the file cannot be decrypted.</summary>
    string Load();
    void Save(string token);
    void Clear();
}

/// <summary>
/// Reads tokens from the "license" edge function:
/// base64url(payload JSON) + "." + base64url(ECDSA P-256 SHA-256 signature
/// over the payload part, r||s). The private key stays in Supabase Vault.
/// </summary>
internal static class LicenseToken
{
    /// <summary>Returns null unless the signature checks out and the payload is complete.</summary>
    public static LicenseGrant Verify(string token, string publicKeySpki)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(publicKeySpki)) return null;
        string[] parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            byte[] signature = Base64Url.DecodeFromChars(parts[1]);
            using (ECDsa key = ECDsa.Create())
            {
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
                if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0]), signature, HashAlgorithmName.SHA256))
                    return null;
            }

            using JsonDocument document = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
            JsonElement root = document.RootElement;
            if (ReadLong(root, "v") != 1) return null;
            string status = ReadString(root, "status");
            string device = ReadString(root, "dev");
            long? issued = ReadLong(root, "iat");
            long? until = ReadLong(root, "until");
            if (status.Length == 0 || device.Length == 0 || issued == null || until == null) return null;
            long? expires = ReadLong(root, "expires");
            return new LicenseGrant(status, ReadString(root, "sub"), device, ReadString(root, "lic"),
                DateTimeOffset.FromUnixTimeSeconds(issued.Value),
                DateTimeOffset.FromUnixTimeSeconds(until.Value),
                expires == null ? null : DateTimeOffset.FromUnixTimeSeconds(expires.Value),
                (int)(ReadLong(root, "max_devices") ?? 0),
                (int)(ReadLong(root, "devices_used") ?? 0),
                ReadString(root, "hint"));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long number)
            ? number
            : null;
}
