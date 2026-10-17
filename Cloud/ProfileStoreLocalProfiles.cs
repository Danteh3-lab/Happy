namespace HappyBot.Cloud;

/// <summary>
/// Local profiles as sync sees them: only the keys <see cref="SettingsCodec"/>
/// owns. Machine settings stored in the same file (HoldButton and friends) are
/// never read for upload and are preserved when a cloud copy is written.
/// </summary>
internal sealed class ProfileStoreLocalProfiles : ILocalProfiles
{
    private readonly ProfileStore _store;

    public ProfileStoreLocalProfiles(ProfileStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public static IEnumerable<string> SyncedKeys => SettingsCodec.EditKeys.Concat(SettingsCodec.CheckKeys);

    public IReadOnlyList<string> Names() => _store.ListProfiles();

    public IReadOnlyDictionary<string, string> Read(string name)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in SyncedKeys)
        {
            string value = _store.Read(name, key);
            if (value.Length > 0) values[key] = value;
        }
        return values;
    }

    public DateTimeOffset LastWriteUtc(string name) => _store.LastWriteUtc(name);

    public void Write(string name, IReadOnlyDictionary<string, string> values)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in SyncedKeys)
        {
            if (values.TryGetValue(key, out string value)) known[key] = value;
        }
        _store.WriteAll(name, known);
    }
}
