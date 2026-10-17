namespace HappyBot.Cloud;

/// <summary>A signed-in Supabase session. Refresh tokens rotate on every refresh.</summary>
internal sealed record CloudSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string UserId,
    string Email)
{
    public bool ExpiresSoon(DateTimeOffset now) => ExpiresAt - now < TimeSpan.FromSeconds(60);
}

/// <summary>One stored profile: the same key/value strings the local INI holds.</summary>
internal sealed record CloudProfile(string Name, IReadOnlyDictionary<string, string> Values, DateTimeOffset UpdatedAt);

/// <summary>Sign-up returns no session while the project requires email confirmation.</summary>
internal sealed record SignUpResult(CloudSession Session, bool ConfirmationRequired);

/// <summary>A cloud call failed. <see cref="Unauthorized"/> means the session is no longer valid.</summary>
internal sealed class CloudException : Exception
{
    public CloudException(string message, bool unauthorized = false, Exception inner = null)
        : base(message, inner)
    {
        Unauthorized = unauthorized;
    }

    public bool Unauthorized { get; }
}

internal interface ISupabaseApi
{
    Task<SignUpResult> SignUpAsync(string email, string password, CancellationToken token);
    Task<CloudSession> SignInAsync(string email, string password, CancellationToken token);
    Task<CloudSession> RefreshAsync(string refreshToken, CancellationToken token);
    Task SignOutAsync(string accessToken, CancellationToken token);
    Task<IReadOnlyList<CloudProfile>> ListProfilesAsync(string accessToken, CancellationToken token);
    Task UpsertProfileAsync(string accessToken, string name, IReadOnlyDictionary<string, string> values, CancellationToken token);
    Task DeleteProfileAsync(string accessToken, string name, CancellationToken token);
}

/// <summary>What survives a restart: never the password, only a rotating refresh token.</summary>
internal sealed record StoredCloudLogin(string Email, string RefreshToken);

internal interface ICloudSessionStore
{
    /// <summary>Returns null when nothing is stored or the file cannot be decrypted.</summary>
    StoredCloudLogin Load();
    void Save(StoredCloudLogin login);
    void Clear();
}

/// <summary>Local profile files, seen as plain key/value maps.</summary>
internal interface ILocalProfiles
{
    IReadOnlyList<string> Names();
    IReadOnlyDictionary<string, string> Read(string name);
    DateTimeOffset LastWriteUtc(string name);
    void Write(string name, IReadOnlyDictionary<string, string> values);
}
