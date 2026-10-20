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
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEoNjyYTB2vfvxvlHakDkrKxTvqm7ndOrpyg3OF+u3lMe5m2+T85PWQYD40r9rv+H+ja/tn0kQc+dWiEGCqKlkVQ==";
}
