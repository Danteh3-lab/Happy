using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HappyBot.Cloud;

/// <summary>
/// The license for this PC: the last signed grant, kept on disk so the bot
/// works offline until the grant's <see cref="LicenseGrant.ValidUntil"/>.
/// UI-thread only.
/// </summary>
internal sealed class LicenseState
{
    /// <summary>A grant older than this is re-checked when the account is online.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);

    /// <summary>A grant issued further in the future than this means the clock was turned back.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly ILicenseTokenStore _store;
    private readonly string _publicKey;

    public LicenseState(ILicenseTokenStore store, string publicKey, string deviceId, string deviceName = "")
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        DeviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        DeviceName = deviceName ?? "";
    }

    public static LicenseState ForApplication() =>
        new(DpapiLicenseTokenStore.ForApplication(), SupabaseProject.LicensePublicKey, DeviceIdentity.Id(),
            Environment.MachineName);

    public string DeviceId { get; }

    public string DeviceName { get; }

    public LicenseGrant Grant { get; private set; }

    /// <summary>Picks up the stored grant, if it is still genuine and for this PC.</summary>
    public void Load()
    {
        Grant = Read(_store.Load());
    }

    /// <summary>Stores a fresh token from the license service.</summary>
    public LicenseGrant Accept(string token)
    {
        LicenseGrant grant = Read(token) ??
            throw new CloudException("The license service returned a token this app cannot verify.");
        Grant = grant;
        _store.Save(token);
        return grant;
    }

    public void Clear()
    {
        Grant = null;
        _store.Clear();
    }

    public bool Allows(DateTimeOffset now) =>
        Grant is { IsActive: true } grant && now >= grant.IssuedAt - ClockSkew && now < grant.ValidUntil;

    public bool NeedsRefresh(DateTimeOffset now) =>
        Grant == null || now - Grant.IssuedAt >= RefreshAfter || now < Grant.IssuedAt - ClockSkew;

    /// <summary>Why the bot cannot start, or "" when it can.</summary>
    public string BlockReason(DateTimeOffset now, bool signedIn)
    {
        if (Allows(now)) return "";
        if (Grant == null)
            return signedIn
                ? "The license has not been checked yet. Stay online for a moment, or redeem a key on the Diagnostics page."
                : "A license is needed to start DANBOT. Sign in and redeem your key on the Diagnostics page.";
        return Describe(now);
    }

    /// <summary>One line for the account card and readiness list.</summary>
    public string Describe(DateTimeOffset now)
    {
        LicenseGrant grant = Grant;
        if (grant == null) return "No license checked on this PC.";
        switch (grant.Status)
        {
            case LicenseGrant.Active when grant.ExpiresAt is { } expires && now >= expires:
                return $"License expired on {FormatDate(expires)}. Redeem a new key.";
            case LicenseGrant.Active when !Allows(now):
                return "License not confirmed recently. Connect to the internet and stay signed in to keep using DANBOT.";
            case LicenseGrant.Active:
                string term = grant.ExpiresAt is { } until ? "Licensed until " + FormatDate(until) : "Lifetime license";
                string devices = grant.MaxDevices == 1 ? "1 PC" : $"{grant.DevicesUsed} of {grant.MaxDevices} PCs";
                return $"{term} · {devices}" + (grant.KeyHint.Length > 0 ? $" · key …{grant.KeyHint}" : "");
            case LicenseGrant.Expired:
                return grant.ExpiresAt is { } ended
                    ? $"License expired on {FormatDate(ended)}. Redeem a new key."
                    : "License expired. Redeem a new key.";
            case LicenseGrant.Revoked:
                return "This license was revoked. Redeem a new key.";
            case LicenseGrant.DeviceLimit:
                return $"This license is already used on {grant.DevicesUsed} of {grant.MaxDevices} PCs. Ask for a device reset to use it here.";
            default:
                return "No license on this account. Redeem a license key.";
        }
    }

    private LicenseGrant Read(string token)
    {
        LicenseGrant grant = LicenseToken.Verify(token, _publicKey);
        return grant != null && string.Equals(grant.DeviceId, DeviceId, StringComparison.OrdinalIgnoreCase) ? grant : null;
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>A stable, anonymous id for this PC: a hash of the Windows machine GUID.</summary>
internal static class DeviceIdentity
{
    public static string Id()
    {
        string machine = null;
        try
        {
            using RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machine = key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            // Fall back to the computer name below.
        }
        if (string.IsNullOrWhiteSpace(machine)) machine = Environment.MachineName;
        return FromMachine(machine);
    }

    internal static string FromMachine(string machine) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("DANBOT-device|" + machine.Trim().ToLowerInvariant())));
}
