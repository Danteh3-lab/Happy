using System.Net;
using System.Text;
using System.Text.Json;
using HappyBot;
using HappyBot.App;
using HappyBot.App.Protocol;
using HappyBot.Cloud;
using HappyBot.Combat;

static partial class Program
{
    private static readonly DateTimeOffset CloudNow = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static void ProfileSyncPlannerCoversEveryCase()
    {
        var older = CloudNow.AddHours(-1);
        var local = new[]
        {
            new LocalProfileSnapshot("Mine", Values(("Left", "100")), older),
            new LocalProfileSnapshot("Same", Values(("Left", "90"), ("Right", "")), older),
            new LocalProfileSnapshot("Cloud newer", Values(("Left", "1")), older),
            new LocalProfileSnapshot("Local newer", Values(("Left", "2")), CloudNow),
            new LocalProfileSnapshot("Never saved", Values(), older),
        };
        var cloud = new[]
        {
            new CloudProfile("theirs", Values(("Left", "120")), older),
            new CloudProfile("same", Values(("Left", "90")), CloudNow),
            new CloudProfile("Cloud newer", Values(("Left", "11")), CloudNow),
            new CloudProfile("Local newer", Values(("Left", "22")), older),
            new CloudProfile("Never saved", Values(("Left", "70")), older),
        };
        var plan = ProfileSyncPlanner.Plan(local, cloud);

        Require(plan.Any(a => a is { Kind: SyncActionKind.Upload, Name: "Mine" }), "a local-only profile uploads");
        Require(plan.Any(a => a is { Kind: SyncActionKind.WriteLocal, Name: "theirs" }), "a cloud-only profile is written locally");
        Require(!plan.Any(a => a.Name.Equals("Same", StringComparison.OrdinalIgnoreCase)),
            "equal values (ignoring empty entries and name case) need no action");
        Require(plan.Any(a => a is { Kind: SyncActionKind.WriteLocal, Name: "Cloud newer" } && a.Values["Left"] == "11") &&
                plan.Any(a => a is { Kind: SyncActionKind.BackupLocal, Name: "Cloud newer backup" } && a.Values["Left"] == "1"),
            "a newer cloud copy wins and the local values are kept as a backup");
        Require(plan.Any(a => a is { Kind: SyncActionKind.Upload, Name: "Local newer" } && a.Values["Left"] == "2") &&
                plan.Any(a => a is { Kind: SyncActionKind.BackupLocal, Name: "Local newer backup" } && a.Values["Left"] == "22"),
            "a newer local copy wins and the cloud values are kept as a backup");
        Require(plan.Any(a => a is { Kind: SyncActionKind.WriteLocal, Name: "Never saved" }) &&
                !plan.Any(a => a.Name == "Never saved backup"),
            "an empty local profile takes the cloud values without a backup");
        Require(plan.Count == 7, "no extra actions: " + plan.Count);

        var taken = new HashSet<string>(new[] { "Duels backup" }, StringComparer.OrdinalIgnoreCase);
        Require(ProfileSyncPlanner.BackupName("Duels", taken) == "Duels backup 2", "backup names avoid existing profiles");
        string longName = ProfileSyncPlanner.BackupName(new string('x', 64), new HashSet<string>());
        Require(longName.Length <= ProfileSyncPlanner.MaxNameLength && longName.EndsWith(" backup"),
            "backup names stay within the cloud name limit");
    }

    private static void SupabaseRestApiShapesRequestsAndErrors()
    {
        var handler = new FakeHttpHandler();
        var api = new SupabaseRestApi(new HttpClient(handler), "https://example.supabase.co/", "pk_test");

        handler.Respond(HttpStatusCode.OK,
            "{\"access_token\":\"a1\",\"refresh_token\":\"r1\",\"expires_at\":1792245242,\"user\":{\"id\":\"u1\",\"email\":\"me@x.test\"}}");
        CloudSession session = api.SignInAsync("me@x.test", "secret", CancellationToken.None).GetAwaiter().GetResult();
        FakeHttpHandler.Captured signIn = handler.Requests.Last();
        Require(signIn.Url == "https://example.supabase.co/auth/v1/token?grant_type=password" && signIn.Method == "POST",
            "sign-in uses the password grant: " + signIn.Url);
        Require(signIn.ApiKey == "pk_test" && signIn.Authorization == null,
            "auth calls carry only the publishable key, never a bearer token");
        Require(signIn.Body.Contains("\"email\":\"me@x.test\"") && signIn.Body.Contains("\"password\":\"secret\""),
            "sign-in sends the credentials as JSON");
        Require(session is { AccessToken: "a1", RefreshToken: "r1", UserId: "u1", Email: "me@x.test" } &&
                session.ExpiresAt == DateTimeOffset.FromUnixTimeSeconds(1792245242),
            "the session is read from the token response");

        handler.Respond(HttpStatusCode.Created, "");
        api.UpsertProfileAsync("a1", "Duels", Values(("Left", "120")), CancellationToken.None).GetAwaiter().GetResult();
        FakeHttpHandler.Captured upsert = handler.Requests.Last();
        Require(upsert.Url == "https://example.supabase.co/rest/v1/bot_profiles?on_conflict=user_id,name" &&
                upsert.Prefer == "resolution=merge-duplicates,return=minimal" && upsert.Authorization == "Bearer a1",
            "upserts merge on (user_id, name) with the user's token");
        using (JsonDocument body = JsonDocument.Parse(upsert.Body))
        {
            JsonElement row = body.RootElement[0];
            Require(row.GetProperty("name").GetString() == "Duels" &&
                    row.GetProperty("settings").GetProperty("Left").GetString() == "120" &&
                    !row.TryGetProperty("user_id", out _),
                "the row carries name and settings; user_id comes from auth.uid()");
        }

        handler.Respond(HttpStatusCode.NoContent, "");
        api.DeleteProfileAsync("a1", "My duels", CancellationToken.None).GetAwaiter().GetResult();
        Require(handler.Requests.Last() is { Method: "DELETE" } d && d.Url.EndsWith("/rest/v1/bot_profiles?name=eq.My%20duels"),
            "deletes filter by the escaped profile name");

        handler.Respond(HttpStatusCode.OK,
            "[{\"name\":\"Duels\",\"settings\":{\"Left\":\"120\",\"Parry\":\"1\"},\"updated_at\":\"2026-10-17T13:00:00+00:00\"}]");
        var listed = api.ListProfilesAsync("a1", CancellationToken.None).GetAwaiter().GetResult();
        Require(listed.Count == 1 && listed[0].Values["Parry"] == "1" &&
                listed[0].UpdatedAt == new DateTimeOffset(2026, 10, 17, 13, 0, 0, TimeSpan.Zero),
            "profiles are listed with their values and update time");

        handler.Respond(HttpStatusCode.BadRequest, "{\"code\":400,\"error_code\":\"invalid_credentials\",\"msg\":\"Invalid login credentials\"}");
        CloudException bad = Catch(() => api.SignInAsync("me@x.test", "wrong", CancellationToken.None).GetAwaiter().GetResult());
        Require(bad is { Message: "Invalid login credentials", Unauthorized: false }, "auth errors surface their message");

        handler.Respond(HttpStatusCode.Unauthorized, "{\"message\":\"JWT expired\"}");
        CloudException expired = Catch(() => api.ListProfilesAsync("old", CancellationToken.None).GetAwaiter().GetResult());
        Require(expired is { Unauthorized: true, Message: "JWT expired" }, "401 marks the session as invalid");

        handler.Respond(HttpStatusCode.OK, "{\"id\":\"u2\",\"email\":\"new@x.test\",\"confirmation_sent_at\":\"2026-10-17T13:00:00Z\"}");
        SignUpResult pending = api.SignUpAsync("new@x.test", "secret1", CancellationToken.None).GetAwaiter().GetResult();
        Require(pending is { ConfirmationRequired: true, Session: null }, "sign-up without a session means email confirmation");

        handler.Throw(new HttpRequestException("offline"));
        CloudException offline = Catch(() => api.SignInAsync("me@x.test", "secret", CancellationToken.None).GetAwaiter().GetResult());
        Require(offline is { Unauthorized: false } && offline.Message.Contains("internet"), "network failures become a readable error");
    }

    private static void DpapiSessionStoreRoundTripsAndRejectsTampering()
    {
        string root = Path.Combine(Path.GetTempPath(), "HappyBot.CloudTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "cloud-session.bin");
        try
        {
            var store = new DpapiCloudSessionStore(path);
            Require(store.Load() == null, "nothing stored yet");
            store.Save(new StoredCloudLogin("me@x.test", "refresh-123"));
            Require(store.Load() is { Email: "me@x.test", RefreshToken: "refresh-123" }, "the login round-trips");
            Require(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("refresh-123"),
                "the refresh token is not stored in plain text");

            byte[] bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            Require(store.Load() == null, "a tampered file is ignored");

            store.Clear();
            Require(!File.Exists(path), "sign-out removes the stored login");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void CloudCoordinatorSignInSyncsAndRecovers()
    {
        var api = new FakeSupabaseApi();
        var store = new MemorySessionStore();
        var local = new MemoryLocalProfiles();
        local.Profiles["Default"] = (Values(("Left", "100")), CloudNow.AddDays(-1));
        api.Cloud.Add(new CloudProfile("Duels", Values(("Left", "120")), CloudNow));
        var toasts = new List<(string Message, string Kind)>();
        var written = new List<string>();
        var cloud = new CloudCoordinator(api, store, new SyncDispatcher(), local, NewLicense(), () => CloudNow);
        cloud.Attach((m, k) => toasts.Add((m, k)), () => { }, names => written.AddRange(names));

        cloud.SignUp("me@x.test", "short");
        Require(api.Calls.Count == 0 && toasts.Last().Kind == "error", "a short password is rejected before any request");

        cloud.SignIn("me@x.test", "secret");
        WaitIdle(cloud);
        Require(cloud.SignedIn && store.Saved?.RefreshToken == "r1", "sign-in stores only the refresh token");
        Require(api.Upserts.Contains("Default") && local.Profiles.ContainsKey("Duels") && written.Contains("Duels"),
            "the first sync uploads local-only profiles and downloads cloud-only ones");
        Require(cloud.Status is { SignedIn: true, Busy: false, Email: "me@x.test" } && cloud.Status.LastSync.Length > 0,
            "status reports the account and the last sync");

        api.Session = api.Session with { ExpiresAt = CloudNow.AddSeconds(30) };
        api.Calls.Clear();
        cloud.SignIn("me@x.test", "secret");
        WaitIdle(cloud);
        Require(api.Calls.SequenceEqual(new[] { "signin", "refresh", "license-check", "list" }) && store.Saved?.RefreshToken == "r2",
            "an expiring token is refreshed (and the rotated refresh token stored) before the next request: " +
            string.Join(",", api.Calls));
        api.Calls.Clear();
        cloud.UploadProfile("Default");
        WaitIdle(cloud);
        Require(api.Calls.SequenceEqual(new[] { "upsert:Default" }), "a fresh token is reused without refreshing");

        api.FailNext = new CloudException("JWT expired", unauthorized: true);
        cloud.Sync();
        WaitIdle(cloud);
        Require(!cloud.SignedIn && store.Saved == null && toasts.Last().Kind == "warning",
            "an expired session signs out and clears the stored login");

        api.Confirm = true;
        cloud.SignUp("new@x.test", "secret1");
        WaitIdle(cloud);
        Require(!cloud.SignedIn && toasts.Last().Message.Contains("confirmation email"),
            "sign-up that needs confirmation does not sign in");
    }

    private static void DeckControllerUsesCloudForProfiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "HappyBot.CloudDeck", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bot = new BotCore(new FakeInputGateway(), new FixedRollSource(0), new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            var profiles = new ProfileStore(root);
            var session = new SettingsSession(new ProfileEditorController(profiles), bot);
            var api = new FakeSupabaseApi();
            var cloud = new CloudCoordinator(api, new MemorySessionStore(), new SyncDispatcher(),
                new ProfileStoreLocalProfiles(profiles), NewLicense(), () => CloudNow);
            var ui = new FakeUiSink();
            using var deck = new DeckController(bot, session, ui, new SyncDispatcher(), new FakeInputEnvironment(),
                new FakeOverlay(), new FakeSound(), new FakeSavePicker(),
                new InputTestRunner(Array.Empty<(string, Action)>(), (_, _) => Task.CompletedTask), cloud);
            deck.Initialize(1920, 1080);
            Require(deck.BuildStatus().Cloud is { Available: true, SignedIn: false }, "status carries the cloud card state");

            api.Cloud.Add(new CloudProfile("Default", Values(("Left", "135"), ("res1", "1920"), ("res2", "1080")), CloudNow.AddYears(1)));
            deck.Handle(new CloudSignInCommand("me@x.test", "secret"));
            WaitIdle(cloud);
            Require(session.Draft.Left == 135 && !session.IsDirty,
                "a newer cloud copy of the clean active profile is loaded into the draft");
            Require(!profiles.ListProfiles().Any(n => n.Contains("backup")),
                "a never-saved local profile is replaced without leaving an empty backup");

            deck.Handle(SettingsJson("{\"Left\":110}"));
            deck.Handle(new ProfileSaveCommand());
            WaitIdle(cloud);
            Require(api.Upserts.Last() == "Default" && api.LastValues["Left"] == "110", "saving a profile uploads it");

            deck.Handle(new ProfileSaveAsCommand("Ranked"));
            WaitIdle(cloud);
            deck.Handle(new ProfileDeleteCommand("Ranked", false, false));
            WaitIdle(cloud);
            Require(api.Upserts.Contains("Ranked") && api.Deletes.Contains("Ranked"), "save-as uploads and delete removes from the cloud");

            deck.Handle(new CloudSignOutCommand());
            WaitIdle(cloud);
            Require(!cloud.SignedIn && File.Exists(profiles.GetProfilePath("Default")), "signing out keeps local profiles");
        }
        finally
        {
            bot.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        WithDeck((deck, ui, _) =>
        {
            deck.Handle(new CloudSyncCommand());
            Require(ui.Messages.OfType<ToastMessage>().Any(t => t.Kind == "error"), "cloud commands without a cloud report an error");
        });
    }

    private static void WaitIdle(CloudCoordinator cloud)
    {
        Require(SpinWait.SpinUntil(() => cloud.LastOperation.IsCompleted && !cloud.Status.Busy, 5000),
            "cloud operation did not finish");
    }

    private static CloudException Catch(Action action)
    {
        try { action(); }
        catch (CloudException ex) { return ex; }
        return null;
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";
        private Exception _throw;

        public sealed record Captured(string Method, string Url, string ApiKey, string Authorization, string Prefer, string Body);

        public List<Captured> Requests { get; } = new();

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
            _throw = null;
        }

        public void Throw(Exception exception) => _throw = exception;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
            Requests.Add(new Captured(request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("apikey", out var key) ? key.Single() : null,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null,
                body));
            if (_throw != null) throw _throw;
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeSupabaseApi : ISupabaseApi
    {
        private int _refreshes;

        public CloudSession Session { get; set; } = new("a1", "r1", CloudNow.AddHours(1), "u1", "me@x.test");
        public bool Confirm { get; set; }
        public CloudException FailNext { get; set; }
        public List<CloudProfile> Cloud { get; } = new();
        public List<string> Calls { get; } = new();
        public List<string> Upserts { get; } = new();
        public List<string> Deletes { get; } = new();
        public IReadOnlyDictionary<string, string> LastValues { get; private set; }
        public string LicenseStatus { get; set; } = "active";
        public CloudException FailLicense { get; set; }
        public string LastKey { get; private set; }

        private void Check()
        {
            CloudException fail = FailNext;
            FailNext = null;
            if (fail != null) throw fail;
        }

        public Task<SignUpResult> SignUpAsync(string email, string password, CancellationToken token)
        {
            Calls.Add("signup");
            return Task.FromResult(Confirm ? new SignUpResult(null, true) : new SignUpResult(Session, false));
        }

        public Task<CloudSession> SignInAsync(string email, string password, CancellationToken token)
        {
            Calls.Add("signin");
            Check();
            return Task.FromResult(Session);
        }

        public Task<CloudSession> RefreshAsync(string refreshToken, CancellationToken token)
        {
            Calls.Add("refresh");
            Check();
            _refreshes++;
            return Task.FromResult(new CloudSession("a" + (_refreshes + 1), "r" + (_refreshes + 1), CloudNow.AddHours(1), "u1", ""));
        }

        public Task SignOutAsync(string accessToken, CancellationToken token)
        {
            Calls.Add("signout");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CloudProfile>> ListProfilesAsync(string accessToken, CancellationToken token)
        {
            Calls.Add("list");
            Check();
            return Task.FromResult<IReadOnlyList<CloudProfile>>(Cloud.ToList());
        }

        public Task UpsertProfileAsync(string accessToken, string name, IReadOnlyDictionary<string, string> values, CancellationToken token)
        {
            Calls.Add("upsert:" + name);
            Check();
            Upserts.Add(name);
            LastValues = values;
            Cloud.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Cloud.Add(new CloudProfile(name, values, CloudNow));
            return Task.CompletedTask;
        }

        public Task DeleteProfileAsync(string accessToken, string name, CancellationToken token)
        {
            Calls.Add("delete:" + name);
            Check();
            Deletes.Add(name);
            Cloud.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<LicenseResult> CheckLicenseAsync(string accessToken, string deviceId, string deviceName, CancellationToken token)
        {
            Calls.Add("license-check");
            return License(deviceId);
        }

        public Task<LicenseResult> RedeemLicenseAsync(string accessToken, string key, string deviceId, string deviceName,
            CancellationToken token)
        {
            Calls.Add("license-redeem");
            LastKey = key;
            return License(deviceId);
        }

        private Task<LicenseResult> License(string deviceId)
        {
            Check();
            CloudException fail = FailLicense;
            FailLicense = null;
            if (fail != null) throw fail;
            return Task.FromResult(new LicenseResult(SignLicense(LicenseStatus, CloudNow, device: deviceId), null));
        }
    }

    private sealed class MemorySessionStore : ICloudSessionStore
    {
        public StoredCloudLogin Saved { get; private set; }
        public StoredCloudLogin Load() => Saved;
        public void Save(StoredCloudLogin login) => Saved = login;
        public void Clear() => Saved = null;
    }

    private sealed class MemoryLocalProfiles : ILocalProfiles
    {
        public Dictionary<string, (IReadOnlyDictionary<string, string> Values, DateTimeOffset Written)> Profiles { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Names() => Profiles.Keys.ToList();
        public IReadOnlyDictionary<string, string> Read(string name) => Profiles[name].Values;
        public DateTimeOffset LastWriteUtc(string name) => Profiles[name].Written;
        public void Write(string name, IReadOnlyDictionary<string, string> values) => Profiles[name] = (values, CloudNow);
    }
}
