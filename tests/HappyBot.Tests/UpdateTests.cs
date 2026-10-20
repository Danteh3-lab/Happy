using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HappyBot.Update;

static partial class Program
{
    private static readonly ECDsa ReleaseSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string ReleasePublicKey = Convert.ToBase64String(ReleaseSigner.ExportSubjectPublicKeyInfo());
    private static readonly string ReleaseSha256 = new('a', 64);

    /// <summary>Signs a manifest the way tools/release.mjs does.</summary>
    private static string SignManifest(object payload, ECDsa signer = null)
    {
        string part = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        byte[] signature = (signer ?? ReleaseSigner).SignData(Encoding.ASCII.GetBytes(part), HashAlgorithmName.SHA256);
        return part + "." + Base64Url.EncodeToString(signature);
    }

    private static Dictionary<string, object> ReleasePayload(string version = "2.0.1",
        string url = "https://example.test/download/DANBOT-2.0.1.exe", long size = 1234, string sha256 = null) =>
        new() { ["v"] = 1, ["version"] = version, ["url"] = url, ["size"] = size, ["sha256"] = sha256 ?? ReleaseSha256 };

    private static void UpdateManifestVerifiesSignatureAndFields()
    {
        UpdateManifest manifest = UpdateManifest.Verify(SignManifest(ReleasePayload()) + "\n", ReleasePublicKey);
        Require(manifest is { Size: 1234 } && manifest.Version == new Version(2, 0, 1) &&
                manifest.Url.AbsoluteUri == "https://example.test/download/DANBOT-2.0.1.exe" &&
                manifest.Sha256 == ReleaseSha256,
            "a genuine manifest is read in full, trailing newline included");

        string token = SignManifest(ReleasePayload());
        string[] parts = token.Split('.');
        string forged = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0])).Replace("2.0.1", "9.9.9")));
        Require(UpdateManifest.Verify(forged + "." + parts[1], ReleasePublicKey) == null, "an edited manifest is rejected");

        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Require(UpdateManifest.Verify(SignManifest(ReleasePayload(), other), ReleasePublicKey) == null,
            "a manifest signed with another key is rejected");
        Require(UpdateManifest.Verify(SignManifest(ReleasePayload(url: "http://example.test/DANBOT.exe")), ReleasePublicKey) == null,
            "plain http downloads are rejected");
        Require(UpdateManifest.Verify(SignManifest(ReleasePayload(sha256: "abc")), ReleasePublicKey) == null,
            "a malformed hash is rejected");
        Require(UpdateManifest.Verify(SignManifest(ReleasePayload(size: 0)), ReleasePublicKey) == null,
            "an empty release is rejected");
        Require(UpdateManifest.Verify(SignManifest(ReleasePayload(version: "latest")), ReleasePublicKey) == null,
            "a non-numeric version is rejected");
        Require(UpdateManifest.Verify("not-a-token", ReleasePublicKey) == null && UpdateManifest.Verify("", ReleasePublicKey) == null,
            "garbage is rejected");
    }

    private static void UpdateManifestOnlyOffersNewerVersions()
    {
        UpdateManifest manifest = UpdateManifest.Verify(SignManifest(ReleasePayload("2.0.1")), ReleasePublicKey);
        Require(manifest.IsNewerThan("2.0.0") && manifest.IsNewerThan("1.9.9"), "a newer release is offered");
        Require(!manifest.IsNewerThan("2.0.1") && !manifest.IsNewerThan("2.1.0"), "the same or an older release is not");
        Require(!manifest.IsNewerThan("") && !manifest.IsNewerThan("dev"), "an unknown current version never updates");
    }
}
