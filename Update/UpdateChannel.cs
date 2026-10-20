namespace HappyBot.Update;

/// <summary>Where DANBOT looks for updates and the key that signs them.</summary>
internal static class UpdateChannel
{
    /// <summary>Signed manifest of the newest release, served by the website's Worker from R2.</summary>
    public const string ManifestUrl = "https://danbot-site.toikromodanick.workers.dev/download/latest.txt";

    /// <summary>
    /// Verifies update manifests (ECDSA P-256, SPKI). The private half exists only on
    /// the release PC (see tools/release-key.mjs); never put it in the repo.
    /// </summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELwr7r0vkyIyse7sHZZBr2ukgFODc0QJ7XfYsf/oEII0YHTdkLjz1nre2kiT1fj4DlpVtjSogsww72hBcFwmPqg==";
}
