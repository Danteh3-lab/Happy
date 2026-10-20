using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HappyBot.Update;

/// <summary>The newest release, as signed by tools/release.mjs.</summary>
internal sealed record UpdateManifest(Version Version, Uri Url, long Size, string Sha256)
{
    /// <summary>
    /// Reads base64url(payload JSON) + "." + base64url(ECDSA P-256 SHA-256 signature
    /// over the payload part, r||s), the same shape as license tokens. Returns null
    /// unless the signature checks out and every field is usable.
    /// </summary>
    public static UpdateManifest Verify(string token, string publicKeySpki)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(publicKeySpki)) return null;
        string[] parts = token.Trim().Split('.');
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
            if (!root.TryGetProperty("v", out JsonElement format) || format.ValueKind != JsonValueKind.Number ||
                !format.TryGetInt32(out int formatVersion) || formatVersion != 1)
                return null;
            if (!Version.TryParse(ReadString(root, "version"), out Version version)) return null;
            if (!Uri.TryCreate(ReadString(root, "url"), UriKind.Absolute, out Uri url) || url.Scheme != Uri.UriSchemeHttps)
                return null;
            if (!root.TryGetProperty("size", out JsonElement sizeValue) || sizeValue.ValueKind != JsonValueKind.Number ||
                !sizeValue.TryGetInt64(out long size) || size <= 0)
                return null;
            string sha256 = ReadString(root, "sha256");
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return null;
            return new UpdateManifest(version, url, size, sha256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True only when <paramref name="currentVersion"/> parses and this release is strictly newer.</summary>
    public bool IsNewerThan(string currentVersion) =>
        Version.TryParse(currentVersion, out Version current) && Version > current;

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
