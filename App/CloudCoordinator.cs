using HappyBot.App.Protocol;
using HappyBot.Cloud;

namespace HappyBot.App;

/// <summary>
/// Account and profile sync for the control deck. Network calls run on the
/// thread pool one at a time; every result, every local file write and every
/// callback happens back on the UI thread through <see cref="IUiDispatcher"/>.
/// Local profile files stay the source of truth: the bot never waits on the
/// cloud, and signing out leaves them untouched.
/// </summary>
internal sealed class CloudCoordinator
{
    public const int MinimumPasswordLength = 6;

    private readonly ISupabaseApi _api;
    private readonly ICloudSessionStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILocalProfiles _local;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sessionSync = new();
    private CloudSession _session;
    private int _pending;
    private string _email = "";
    private string _error = "";
    private DateTimeOffset? _lastSync;
    private Action<string, string> _toast = (_, _) => { };
    private Action _statusChanged = () => { };
    private Action<IReadOnlyCollection<string>> _profilesWritten = _ => { };

    public CloudCoordinator(ISupabaseApi api, ICloudSessionStore store, IUiDispatcher dispatcher, ILocalProfiles local,
        Func<DateTimeOffset> clock = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool SignedIn
    {
        get { lock (_sessionSync) return _session != null; }
    }

    public CloudStatusDto Status => new()
    {
        Available = true,
        SignedIn = SignedIn,
        Email = _email,
        Busy = Volatile.Read(ref _pending) > 0,
        LastSync = _lastSync?.ToLocalTime().ToString("HH:mm") ?? "",
        Error = _error
    };

    /// <summary>Wires UI callbacks; called once by the deck controller.</summary>
    public void Attach(Action<string, string> toast, Action statusChanged, Action<IReadOnlyCollection<string>> profilesWritten)
    {
        _toast = toast ?? throw new ArgumentNullException(nameof(toast));
        _statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
        _profilesWritten = profilesWritten ?? throw new ArgumentNullException(nameof(profilesWritten));
    }

    /// <summary>Resumes the stored login at startup and syncs quietly.</summary>
    public void Restore()
    {
        StoredCloudLogin login = _store.Load();
        if (login == null) return;
        _email = login.Email ?? "";
        Run(token => _api.RefreshAsync(login.RefreshToken, token), session =>
        {
            SetSession(session);
            Sync(quiet: true);
        }, "Cloud sign-in");
    }

    public void SignIn(string email, string password)
    {
        email = (email ?? "").Trim();
        if (email.Length == 0 || string.IsNullOrEmpty(password))
        {
            _toast("Enter an email and password.", "error");
            return;
        }
        Run(token => _api.SignInAsync(email, password, token), session =>
        {
            SetSession(session);
            _toast($"Signed in as {_email}.", "success");
            Sync();
        }, "Sign-in failed");
    }

    public void SignUp(string email, string password)
    {
        email = (email ?? "").Trim();
        if (email.Length == 0 || string.IsNullOrEmpty(password))
        {
            _toast("Enter an email and password.", "error");
            return;
        }
        if (password.Length < MinimumPasswordLength)
        {
            _toast($"Use a password of at least {MinimumPasswordLength} characters.", "error");
            return;
        }
        Run(token => _api.SignUpAsync(email, password, token), result =>
        {
            if (result.ConfirmationRequired)
            {
                _email = email;
                _toast($"Account created. Open the confirmation email sent to {email}, then sign in here.", "success");
                return;
            }
            SetSession(result.Session);
            _toast($"Account created. Signed in as {_email}.", "success");
            Sync();
        }, "Account creation failed");
    }

    public void SignOut()
    {
        CloudSession session;
        lock (_sessionSync) session = _session;
        ClearSession();
        _toast("Signed out. Local profiles stay on this PC.", "info");
        _statusChanged();
        if (session == null) return;
        Run(async token =>
        {
            try { await _api.SignOutAsync(session.AccessToken, token).ConfigureAwait(false); }
            catch (CloudException) { }
            return true;
        }, _ => { }, "Sign-out");
    }

    /// <summary>Two-way sync of every local profile against the account.</summary>
    public void Sync(bool quiet = false)
    {
        if (!SignedIn)
        {
            if (!quiet) _toast("Sign in to sync profiles.", "info");
            return;
        }
        List<LocalProfileSnapshot> local = SnapshotLocal();
        Run(async token =>
        {
            string access = await AccessTokenAsync(token).ConfigureAwait(false);
            IReadOnlyList<CloudProfile> cloud = await _api.ListProfilesAsync(access, token).ConfigureAwait(false);
            IReadOnlyList<SyncAction> plan = ProfileSyncPlanner.Plan(local, cloud);
            foreach (SyncAction upload in plan.Where(action => action.Kind == SyncActionKind.Upload))
                await _api.UpsertProfileAsync(access, upload.Name, upload.Values, token).ConfigureAwait(false);
            return plan;
        }, plan => ApplyPlan(plan, quiet), "Cloud sync failed");
    }

    /// <summary>Uploads one profile after it was saved locally.</summary>
    public void UploadProfile(string name)
    {
        if (!SignedIn) return;
        IReadOnlyDictionary<string, string> values = _local.Read(name);
        Run(async token =>
        {
            string access = await AccessTokenAsync(token).ConfigureAwait(false);
            await _api.UpsertProfileAsync(access, name, values, token).ConfigureAwait(false);
            return true;
        }, _ => _lastSync = _clock(), "Cloud upload failed");
    }

    /// <summary>Removes a profile from the account after it was deleted locally.</summary>
    public void DeleteProfile(string name)
    {
        if (!SignedIn) return;
        Run(async token =>
        {
            string access = await AccessTokenAsync(token).ConfigureAwait(false);
            await _api.DeleteProfileAsync(access, name, token).ConfigureAwait(false);
            return true;
        }, _ => _lastSync = _clock(), "Cloud delete failed");
    }

    /// <summary>For tests: completes when the most recently started operation has finished.</summary>
    internal Task LastOperation { get; private set; } = Task.CompletedTask;

    private void ApplyPlan(IReadOnlyList<SyncAction> plan, bool quiet)
    {
        var written = new List<string>();
        int failed = 0;
        foreach (SyncAction action in plan.Where(action => action.Kind != SyncActionKind.Upload))
        {
            try
            {
                _local.Write(action.Name, action.Values);
                written.Add(action.Name);
            }
            catch
            {
                failed++;
            }
        }
        _lastSync = _clock();
        if (written.Count > 0) _profilesWritten(written);

        int uploaded = plan.Count(action => action.Kind == SyncActionKind.Upload);
        int downloaded = plan.Count(action => action.Kind == SyncActionKind.WriteLocal);
        int backups = plan.Count(action => action.Kind == SyncActionKind.BackupLocal);
        if (failed > 0)
            _toast($"Cloud sync: {failed} profile(s) could not be written on this PC.", "error");
        else if (plan.Count > 0)
            _toast(DescribeSync(uploaded, downloaded, backups), "success");
        else if (!quiet)
            _toast("Cloud profiles are up to date.", "success");
    }

    internal static string DescribeSync(int uploaded, int downloaded, int backups)
    {
        var parts = new List<string>();
        if (uploaded > 0) parts.Add($"{uploaded} uploaded");
        if (downloaded > 0) parts.Add($"{downloaded} downloaded");
        if (backups > 0) parts.Add(backups == 1 ? "1 conflict kept as a backup profile" : $"{backups} conflicts kept as backup profiles");
        return "Cloud sync: " + string.Join(", ", parts) + ".";
    }

    private List<LocalProfileSnapshot> SnapshotLocal()
    {
        var snapshots = new List<LocalProfileSnapshot>();
        foreach (string name in _local.Names())
            snapshots.Add(new LocalProfileSnapshot(name, _local.Read(name), _local.LastWriteUtc(name)));
        return snapshots;
    }

    private async Task<string> AccessTokenAsync(CancellationToken token)
    {
        CloudSession session;
        lock (_sessionSync) session = _session;
        if (session == null) throw new CloudException("Sign in to use cloud profiles.", unauthorized: true);
        if (!session.ExpiresSoon(_clock())) return session.AccessToken;

        CloudSession fresh = await _api.RefreshAsync(session.RefreshToken, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(fresh.Email)) fresh = fresh with { Email = session.Email };
        lock (_sessionSync)
        {
            if (_session == null) throw new CloudException("Signed out.", unauthorized: true);
            _session = fresh;
        }
        _store.Save(new StoredCloudLogin(fresh.Email, fresh.RefreshToken));
        return fresh.AccessToken;
    }

    private void SetSession(CloudSession session)
    {
        lock (_sessionSync) _session = session;
        _email = session.Email ?? "";
        _error = "";
        _store.Save(new StoredCloudLogin(_email, session.RefreshToken));
    }

    private void ClearSession()
    {
        lock (_sessionSync) _session = null;
        _store.Clear();
    }

    private void Run<T>(Func<CancellationToken, Task<T>> work, Action<T> onSuccess, string failurePrefix)
    {
        Interlocked.Increment(ref _pending);
        _statusChanged();
        LastOperation = Task.Run(async () =>
        {
            T result = default;
            Exception failure = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                result = await work(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                _gate.Release();
            }

            _dispatcher.Post(() =>
            {
                try
                {
                    if (failure == null)
                    {
                        _error = "";
                        onSuccess(result);
                    }
                    else
                    {
                        Fail(failurePrefix, failure);
                    }
                }
                finally
                {
                    // Decrement after the callback so a chained operation (sign in → sync)
                    // never shows a moment of "idle" between the two.
                    Interlocked.Decrement(ref _pending);
                    _statusChanged();
                }
            });
        });
    }

    private void Fail(string prefix, Exception failure)
    {
        if (failure is CloudException { Unauthorized: true })
        {
            ClearSession();
            _error = "Signed out: the cloud session expired.";
            _toast("Cloud session expired. Sign in again; local profiles are unchanged.", "warning");
            return;
        }
        string message = failure is CloudException ? failure.Message : "Unexpected error: " + failure.Message;
        _error = message;
        _toast(prefix + ": " + message, "error");
    }
}
