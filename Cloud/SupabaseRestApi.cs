using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HappyBot.Cloud;

/// <summary>
/// Minimal Supabase client over the Auth (GoTrue) and PostgREST HTTP APIs.
/// Auth calls carry only the publishable key; table calls add the user's
/// access token, so row-level security applies to every request.
/// </summary>
internal sealed class SupabaseRestApi : ISupabaseApi
{
    private const string ProfilesPath = "/rest/v1/bot_profiles";
    private const string LicensePath = "/functions/v1/license";

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _key;

    public SupabaseRestApi(HttpClient http, string url, string key)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _url = (url ?? throw new ArgumentNullException(nameof(url))).TrimEnd('/');
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public static SupabaseRestApi ForProject() =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, SupabaseProject.Url, SupabaseProject.PublishableKey);

    public async Task<SignUpResult> SignUpAsync(string email, string password, CancellationToken token)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Post, "/auth/v1/signup", null,
            new { email, password }, null, token).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        return root.TryGetProperty("access_token", out _)
            ? new SignUpResult(ReadSession(root), false)
            : new SignUpResult(null, true);
    }

    public async Task<CloudSession> SignInAsync(string email, string password, CancellationToken token)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=password", null,
            new { email, password }, null, token).ConfigureAwait(false);
        return ReadSession(document.RootElement);
    }

    public async Task<CloudSession> RefreshAsync(string refreshToken, CancellationToken token)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token", null,
            new { refresh_token = refreshToken }, null, token).ConfigureAwait(false);
        return ReadSession(document.RootElement);
    }

    public async Task SignOutAsync(string accessToken, CancellationToken token)
    {
        try
        {
            using JsonDocument _ = await SendAsync(HttpMethod.Post, "/auth/v1/logout", accessToken, null, null, token)
                .ConfigureAwait(false);
        }
        catch (CloudException ex) when (ex.Unauthorized)
        {
            // Already invalid on the server; signing out locally is enough.
        }
    }

    public async Task<IReadOnlyList<CloudProfile>> ListProfilesAsync(string accessToken, CancellationToken token)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Get, ProfilesPath + "?select=name,settings,updated_at",
            accessToken, null, null, token).ConfigureAwait(false);
        var profiles = new List<CloudProfile>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return profiles;
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            string name = row.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
            if (name.Length == 0) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (row.TryGetProperty("settings", out JsonElement settings) && settings.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in settings.EnumerateObject())
                {
                    values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? ""
                        : property.Value.GetRawText();
                }
            }
            DateTimeOffset updatedAt = row.TryGetProperty("updated_at", out JsonElement updated) &&
                DateTimeOffset.TryParse(updated.GetString(), out DateTimeOffset parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
            profiles.Add(new CloudProfile(name, values, updatedAt));
        }
        return profiles;
    }

    public async Task UpsertProfileAsync(string accessToken, string name, IReadOnlyDictionary<string, string> values,
        CancellationToken token)
    {
        // user_id defaults to auth.uid() before the (user_id, name) conflict check runs.
        using JsonDocument _ = await SendAsync(HttpMethod.Post, ProfilesPath + "?on_conflict=user_id,name", accessToken,
            new[] { new { name, settings = values } }, "resolution=merge-duplicates,return=minimal", token)
            .ConfigureAwait(false);
    }

    public async Task DeleteProfileAsync(string accessToken, string name, CancellationToken token)
    {
        using JsonDocument _ = await SendAsync(HttpMethod.Delete, ProfilesPath + "?name=eq." + Uri.EscapeDataString(name),
            accessToken, null, "return=minimal", token).ConfigureAwait(false);
    }

    public Task<string> CheckLicenseAsync(string accessToken, string deviceId, string deviceName, CancellationToken token) =>
        LicenseAsync(accessToken, new { action = "check", device_id = deviceId, device_name = deviceName }, token);

    public Task<string> RedeemLicenseAsync(string accessToken, string key, string deviceId, string deviceName,
        CancellationToken token) =>
        LicenseAsync(accessToken, new { action = "redeem", key, device_id = deviceId, device_name = deviceName }, token);

    private async Task<string> LicenseAsync(string accessToken, object body, CancellationToken token)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Post, LicensePath, accessToken, body, null, token)
            .ConfigureAwait(false);
        string licenseToken = ReadString(document.RootElement, "token");
        if (licenseToken.Length == 0) throw new CloudException("The license service returned no token.");
        return licenseToken;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string accessToken, object body,
        string prefer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, _url + path);
        request.Headers.Add("apikey", _key);
        if (accessToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (prefer != null) request.Headers.Add("Prefer", prefer);
        if (body != null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            throw new CloudException("Could not reach the cloud service. Check the internet connection.", false, ex);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                bool unauthorized = response.StatusCode == HttpStatusCode.Unauthorized;
                throw new CloudException(ReadError(text, response.StatusCode), unauthorized);
            }
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
    }

    private static CloudSession ReadSession(JsonElement root)
    {
        string accessToken = ReadString(root, "access_token");
        string refreshToken = ReadString(root, "refresh_token");
        if (accessToken.Length == 0 || refreshToken.Length == 0)
            throw new CloudException("The cloud service returned no session.");
        DateTimeOffset expiresAt = root.TryGetProperty("expires_at", out JsonElement at) && at.TryGetInt64(out long unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out JsonElement inSeconds) &&
                inSeconds.TryGetInt32(out int seconds) ? seconds : 3600);
        JsonElement user = root.TryGetProperty("user", out JsonElement u) ? u : default;
        return new CloudSession(accessToken, refreshToken, expiresAt,
            user.ValueKind == JsonValueKind.Object ? ReadString(user, "id") : "",
            user.ValueKind == JsonValueKind.Object ? ReadString(user, "email") : "");
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>Auth and PostgREST use different error shapes; take the first human message.</summary>
    internal static string ReadError(string body, HttpStatusCode status)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            foreach (string key in new[] { "msg", "error_description", "message", "error" })
            {
                string message = ReadString(document.RootElement, key);
                if (message.Length > 0) return message;
            }
        }
        catch (JsonException)
        {
        }
        return "Cloud request failed (" + (int)status + ").";
    }
}
