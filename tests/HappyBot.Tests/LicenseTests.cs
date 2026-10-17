using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HappyBot;
using HappyBot.App;
using HappyBot.App.Protocol;
using HappyBot.Cloud;
using HappyBot.Combat;

static partial class Program
{
    private static readonly ECDsa LicenseSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string LicensePublicKey = Convert.ToBase64String(LicenseSigner.ExportSubjectPublicKeyInfo());
    private static readonly string TestDevice = DeviceIdentity.FromMachine("test-machine");

    /// <summary>Signs a token the way the license edge function does.</summary>
    private static string SignLicense(string status, DateTimeOffset issued, DateTimeOffset? expires = null,
        string device = null, int maxDevices = 1, int devicesUsed = 1, ECDsa signer = null)
    {
        long iat = issued.ToUnixTimeSeconds();
        long grace = iat + 72 * 3600;
        var payload = new Dictionary<string, object>
        {
            ["v"] = 1, ["sub"] = "u1", ["dev"] = device ?? TestDevice, ["status"] = status, ["lic"] = "lic-1",
            ["iat"] = iat, ["until"] = expires is { } e ? Math.Min(grace, e.ToUnixTimeSeconds()) : grace,
            ["expires"] = expires?.ToUnixTimeSeconds(), ["max_devices"] = maxDevices, ["devices_used"] = devicesUsed,
            ["hint"] = "WEJX"
        };
        string part = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        byte[] signature = (signer ?? LicenseSigner).SignData(Encoding.ASCII.GetBytes(part), HashAlgorithmName.SHA256);
        return part + "." + Base64Url.EncodeToString(signature);
    }

    private static LicenseState NewLicense(MemoryLicenseStore store = null) =>
        new(store ?? new MemoryLicenseStore(), LicensePublicKey, TestDevice, "TEST-PC");

    private static void LicenseTokenVerifiesSignatureDeviceAndTime()
    {
        string token = SignLicense("active", CloudNow, CloudNow.AddDays(30), maxDevices: 2, devicesUsed: 1);
        LicenseGrant grant = LicenseToken.Verify(token, LicensePublicKey);
        Require(grant is { IsActive: true, LicenseId: "lic-1", MaxDevices: 2, DevicesUsed: 1, KeyHint: "WEJX" } &&
                grant.IssuedAt == CloudNow && grant.ValidUntil == CloudNow.AddHours(72) &&
                grant.ExpiresAt == CloudNow.AddDays(30),
            "a genuine token is read in full");

        string[] parts = token.Split('.');
        string forged = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[0])).Replace("\"max_devices\":2", "\"max_devices\":99")));
        Require(LicenseToken.Verify(forged + "." + parts[1], LicensePublicKey) == null, "an edited payload is rejected");
        using (ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            Require(LicenseToken.Verify(SignLicense("active", CloudNow, signer: other), LicensePublicKey) == null,
                "a token signed by another key is rejected");
        Require(LicenseToken.Verify("garbage", LicensePublicKey) == null && LicenseToken.Verify("a.b", LicensePublicKey) == null,
            "malformed tokens are rejected without throwing");
        Require(LicenseToken.Verify(SignLicense("active", CloudNow), SupabaseProject.LicensePublicKey) == null,
            "the shipped public key does not accept test-signed tokens");

        var store = new MemoryLicenseStore();
        LicenseState license = NewLicense(store);
        Require(!license.Allows(CloudNow) && license.NeedsRefresh(CloudNow), "no grant: blocked and due for a check");
        Require(Catch(() => license.Accept(SignLicense("active", CloudNow, device: DeviceIdentity.FromMachine("other")))) != null &&
                store.Token == null,
            "a grant for another PC is refused and not stored");

        license.Accept(SignLicense("active", CloudNow));
        Require(license.Allows(CloudNow) && license.Allows(CloudNow.AddHours(71)) && !license.Allows(CloudNow.AddHours(72)),
            "a lifetime grant works offline for 72 hours");
        Require(!license.Allows(CloudNow.AddMinutes(-10)) && license.NeedsRefresh(CloudNow.AddMinutes(-10)),
            "turning the clock back before the grant blocks and asks for a new check");
        Require(!license.NeedsRefresh(CloudNow.AddHours(5)) && license.NeedsRefresh(CloudNow.AddHours(6)),
            "a grant is re-checked after six hours");
        Require(license.Describe(CloudNow) == "Lifetime license · 1 PC · key …WEJX", license.Describe(CloudNow));

        LicenseState reloaded = NewLicense(store);
        reloaded.Load();
        Require(reloaded.Allows(CloudNow.AddHours(1)), "the stored grant survives a restart");

        license.Accept(SignLicense("active", CloudNow, CloudNow.AddHours(2)));
        Require(license.Allows(CloudNow.AddHours(1)) && !license.Allows(CloudNow.AddHours(2)) &&
                license.Describe(CloudNow.AddHours(3)).StartsWith("License expired"),
            "a timed license stops at its expiry even inside the offline window");

        license.Accept(SignLicense("active", CloudNow, CloudNow.AddDays(30)));
        Require(license.Remaining(CloudNow) == "30 days left", license.Remaining(CloudNow));
        Require(LicenseState.FormatLeft(TimeSpan.FromHours(36), false) == "1 day" &&
                LicenseState.FormatLeft(TimeSpan.FromHours(23.5), false) == "23 h" &&
                LicenseState.FormatLeft(TimeSpan.FromMinutes(90), false) == "1 h" &&
                LicenseState.FormatLeft(TimeSpan.FromSeconds(61), false) == "2 min" &&
                LicenseState.FormatLeft(TimeSpan.FromHours(64), true) == "64 h",
            "the countdown goes from days to hours to minutes");
        Require(!license.Expiring(CloudNow) && license.ConfirmWithin(CloudNow.AddHours(1)) == "",
            "a fresh 30-day license is neither expiring nor offline");
        Require(license.ConfirmWithin(CloudNow.AddHours(8)) == "Offline · confirm within 64 h" &&
                !license.Expiring(CloudNow.AddHours(8)) && license.Expiring(CloudNow.AddHours(50)),
            "a missed check shows the offline countdown, warning in the last 24 hours");
        Require(license.Remaining(CloudNow.AddHours(72)) == "" && license.ConfirmWithin(CloudNow.AddHours(72)) == "",
            "no countdown once the license no longer works");

        license.Accept(SignLicense("active", CloudNow, CloudNow.AddDays(2)));
        Require(license.Remaining(CloudNow) == "2 days left" && license.Expiring(CloudNow) &&
                license.ConfirmWithin(CloudNow.AddHours(10)) == "",
            "under three days left warns, and the expiry (not the offline window) is the countdown");

        license.Accept(SignLicense("active", CloudNow));
        Require(license.Remaining(CloudNow) == "" && license.ConfirmWithin(CloudNow.AddHours(7)) == "Offline · confirm within 65 h",
            "a lifetime license has no expiry countdown, only the offline one");

        license.Accept(SignLicense("device_limit", CloudNow, maxDevices: 2, devicesUsed: 2));
        Require(!license.Allows(CloudNow) && license.Describe(CloudNow).Contains("2 of 2 PCs"), "device limit blocks and explains");
        license.Accept(SignLicense("none", CloudNow));
        Require(!license.Allows(CloudNow) && license.BlockReason(CloudNow, signedIn: true).Contains("Redeem"),
            "no license on the account asks for a key");

        license.Clear();
        Require(store.Token == null && license.Grant == null, "clearing forgets the grant");
        Require(license.BlockReason(CloudNow, signedIn: false).Contains("Sign in"), "signed out: the reason says to sign in");
        Require(DeviceIdentity.FromMachine("ABC") == DeviceIdentity.FromMachine(" abc ") && TestDevice.Length == 64,
            "device ids are stable 64-char hashes");
    }

    private static void SupabaseRestApiCallsLicenseFunction()
    {
        var handler = new FakeHttpHandler();
        var api = new SupabaseRestApi(new HttpClient(handler), "https://example.supabase.co", "pk_test");

        handler.Respond(HttpStatusCode.OK, "{\"token\":\"t.1\",\"status\":\"active\"}");
        string token = api.RedeemLicenseAsync("a1", "DANBOT-AAAAA", TestDevice, "PC", CancellationToken.None).GetAwaiter().GetResult();
        FakeHttpHandler.Captured redeem = handler.Requests.Last();
        Require(token == "t.1" && redeem.Url == "https://example.supabase.co/functions/v1/license" && redeem.Method == "POST" &&
                redeem.Authorization == "Bearer a1" && redeem.ApiKey == "pk_test",
            "redeem posts to the license function with the user's token");
        using (JsonDocument body = JsonDocument.Parse(redeem.Body))
            Require(body.RootElement.GetProperty("action").GetString() == "redeem" &&
                    body.RootElement.GetProperty("key").GetString() == "DANBOT-AAAAA" &&
                    body.RootElement.GetProperty("device_id").GetString() == TestDevice,
                "redeem sends the key and this PC's id");

        handler.Respond(HttpStatusCode.OK, "{\"token\":\"t.2\"}");
        api.CheckLicenseAsync("a1", TestDevice, "PC", CancellationToken.None).GetAwaiter().GetResult();
        Require(handler.Requests.Last().Body.Contains("\"action\":\"check\"") && !handler.Requests.Last().Body.Contains("\"key\""),
            "check sends no key");

        handler.Respond(HttpStatusCode.BadRequest, "{\"error\":\"License key not found.\"}");
        CloudException missing = Catch(() => api.CheckLicenseAsync("a1", TestDevice, "PC", CancellationToken.None).GetAwaiter().GetResult());
        Require(missing is { Message: "License key not found.", Unauthorized: false }, "license errors surface their message");
    }

    private static void CloudCoordinatorChecksAndRedeemsLicense()
    {
        DateTimeOffset now = CloudNow;
        var api = new FakeSupabaseApi { LicenseStatus = "none" };
        var store = new MemoryLicenseStore();
        var toasts = new List<(string Message, string Kind)>();
        var cloud = new CloudCoordinator(api, new MemorySessionStore(), new SyncDispatcher(), new MemoryLocalProfiles(),
            NewLicense(store), () => now);
        cloud.Attach((m, k) => toasts.Add((m, k)), () => { }, _ => { });

        cloud.RedeemLicense("DANBOT-AAAAA");
        Require(api.Calls.Count == 0 && toasts.Last().Kind == "error", "redeeming needs an account first");
        Require(cloud.Status.License is { Active: false, State: "unchecked" } && cloud.LicenseBlockReason.Contains("Sign in"),
            "signed out: no license and the reason says to sign in");

        cloud.SignIn("me@x.test", "secret");
        WaitIdle(cloud);
        Require(api.Calls.Contains("license-check") && !cloud.LicenseActive && cloud.Status.License.State == "none" &&
                toasts.Any(t => t.Kind == "warning" && t.Message.Contains("Redeem")),
            "sign-in checks the license and says when the account has none");

        api.LicenseStatus = "active";
        cloud.RedeemLicense("DANBOT-AAAAA");
        WaitIdle(cloud);
        Require(api.LastKey == "DANBOT-AAAAA" && cloud.LicenseActive && store.Token != null &&
                cloud.Status.License is { Active: true, State: "active" } && toasts.Last().Kind == "success",
            "redeeming a key activates the license and stores the grant");

        api.Calls.Clear();
        now = CloudNow.AddHours(1);
        cloud.OnTick();
        WaitIdle(cloud);
        Require(!api.Calls.Contains("license-check"), "a fresh grant is not re-checked");

        now = CloudNow.AddHours(7);
        api.FailLicense = new CloudException("Could not reach the cloud service. Check the internet connection.");
        int toastCount = toasts.Count;
        cloud.OnTick();
        WaitIdle(cloud);
        Require(api.Calls.Contains("license-check") && toasts.Count == toastCount && cloud.LicenseActive,
            "an offline background check is silent and the grant keeps working");
        api.Calls.Clear();
        cloud.OnTick();
        WaitIdle(cloud);
        Require(!api.Calls.Contains("license-check"), "a failed background check waits before retrying");

        now += CloudCoordinator.LicenseRetry;
        api.LicenseStatus = "revoked";
        cloud.OnTick();
        WaitIdle(cloud);
        Require(!cloud.LicenseActive && toasts.Last() is { Kind: "warning" } t && t.Message.Contains("revoked"),
            "a revoked license is picked up on the next check and announced");

        api.FailLicense = new CloudException("This license key is already used by another account.");
        cloud.RedeemLicense("DANBOT-BBBBB");
        WaitIdle(cloud);
        Require(toasts.Last() is { Kind: "error" } taken && taken.Message.Contains("another account"),
            "a refused key reports why");

        api.LicenseStatus = "active";
        cloud.RedeemLicense("DANBOT-CCCCC");
        WaitIdle(cloud);
        cloud.SignOut();
        WaitIdle(cloud);
        Require(!cloud.LicenseActive && store.Token == null, "signing out removes the license from this PC");
    }

    private static void DeckControllerRequiresLicenseToStart()
    {
        DateTimeOffset now = CloudNow;
        var input = new FakeInputEnvironment { IsReady = false };
        var api = new FakeSupabaseApi();
        var cloud = new CloudCoordinator(api, new MemorySessionStore(), new SyncDispatcher(), new MemoryLocalProfiles(),
            NewLicense(), () => now);
        using var harness = new DeckHarness(input, cloud);
        DeckController deck = harness.Deck;
        FakeUiSink ui = harness.Ui;

        deck.Handle(new StartCommand("s1"));
        ApplyResultMessage refused = ui.Messages.OfType<ApplyResultMessage>().Last();
        Require(!refused.Success && refused.Message.Contains("license") && !harness.Bot.IsRunning,
            "Start is refused without a license: " + refused.Message);
        Require(deck.BuildStatus().Cloud.License is { Active: false }, "status reports the missing license");

        deck.Handle(new CloudSignInCommand("me@x.test", "secret"));
        WaitIdle(cloud);
        deck.Handle(new LicenseRedeemCommand("DANBOT-AAAAA"));
        WaitIdle(cloud);
        Require(deck.BuildStatus().Cloud.License is { Active: true }, "redeeming through the deck activates the license");

        deck.Handle(new StartCommand("s2"));
        ApplyResultMessage next = ui.Messages.OfType<ApplyResultMessage>().Last();
        Require(next.RequestId == "s2" && next.Message.Contains("ViGEm"),
            "with a license, Start moves on to the next check: " + next.Message);

        now = CloudNow.AddHours(73);
        api.FailLicense = new CloudException("offline");
        deck.OnStatusTick();
        WaitIdle(cloud);
        deck.Handle(new StartCommand("s3"));
        Require(ui.Messages.OfType<ApplyResultMessage>().Last().Message.Contains("not confirmed"),
            "after three days without a check the license blocks Start again");
        Require(new LicenseRedeemCommand("DANBOT-SECRET").ToString().IndexOf("SECRET", StringComparison.Ordinal) < 0,
            "the key is kept out of logged command text");
    }

    private sealed class MemoryLicenseStore : ILicenseTokenStore
    {
        public string Token { get; private set; }
        public string Load() => Token;
        public void Save(string token) => Token = token;
        public void Clear() => Token = null;
    }
}
