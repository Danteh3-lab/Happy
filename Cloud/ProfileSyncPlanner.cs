namespace HappyBot.Cloud;

internal sealed record LocalProfileSnapshot(string Name, IReadOnlyDictionary<string, string> Values, DateTimeOffset LastWriteUtc);

internal enum SyncActionKind
{
    /// <summary>Send the local values to the cloud.</summary>
    Upload,
    /// <summary>Overwrite (or create) the local profile with cloud values.</summary>
    WriteLocal,
    /// <summary>Keep the losing side of a conflict as a new local profile.</summary>
    BackupLocal
}

internal sealed record SyncAction(SyncActionKind Kind, string Name, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Pure sync rules. Profile names match case-insensitively, like local files.
///   local only            → upload
///   cloud only            → write locally
///   same values           → nothing
///   one side empty        → copy the side that has values (nothing to protect)
///   different values      → the newer side wins; the other side is kept as
///                           "&lt;name&gt; backup" so a conflict never loses settings
/// </summary>
internal static class ProfileSyncPlanner
{
    public const int MaxNameLength = 64;

    public static IReadOnlyList<SyncAction> Plan(IReadOnlyList<LocalProfileSnapshot> local, IReadOnlyList<CloudProfile> cloud)
    {
        var localByName = new Dictionary<string, LocalProfileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (LocalProfileSnapshot profile in local) localByName[profile.Name] = profile;
        var cloudByName = new Dictionary<string, CloudProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (CloudProfile profile in cloud) cloudByName[profile.Name] = profile;

        var taken = new HashSet<string>(localByName.Keys, StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(cloudByName.Keys);
        var actions = new List<SyncAction>();

        foreach (LocalProfileSnapshot mine in local)
        {
            if (!cloudByName.TryGetValue(mine.Name, out CloudProfile theirs))
            {
                actions.Add(new SyncAction(SyncActionKind.Upload, mine.Name, mine.Values));
                continue;
            }
            if (SameValues(mine.Values, theirs.Values)) continue;
            if (Meaningful(mine.Values).Count == 0)
            {
                actions.Add(new SyncAction(SyncActionKind.WriteLocal, mine.Name, theirs.Values));
                continue;
            }
            if (Meaningful(theirs.Values).Count == 0)
            {
                actions.Add(new SyncAction(SyncActionKind.Upload, mine.Name, mine.Values));
                continue;
            }

            string backup = BackupName(mine.Name, taken);
            taken.Add(backup);
            if (theirs.UpdatedAt > mine.LastWriteUtc)
            {
                actions.Add(new SyncAction(SyncActionKind.BackupLocal, backup, mine.Values));
                actions.Add(new SyncAction(SyncActionKind.WriteLocal, mine.Name, theirs.Values));
            }
            else
            {
                actions.Add(new SyncAction(SyncActionKind.BackupLocal, backup, theirs.Values));
                actions.Add(new SyncAction(SyncActionKind.Upload, mine.Name, mine.Values));
            }
        }

        foreach (CloudProfile theirs in cloud)
        {
            if (!localByName.ContainsKey(theirs.Name))
                actions.Add(new SyncAction(SyncActionKind.WriteLocal, theirs.Name, theirs.Values));
        }
        return actions;
    }

    /// <summary>Empty values mean "unset" in a profile file, so they never count as a difference.</summary>
    public static bool SameValues(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        var left = Meaningful(a);
        var right = Meaningful(b);
        if (left.Count != right.Count) return false;
        foreach ((string key, string value) in left)
        {
            if (!right.TryGetValue(key, out string other) || !string.Equals(value, other, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>"Name backup", then "Name backup 2", ... trimmed to the cloud name limit.</summary>
    public static string BackupName(string name, ISet<string> taken)
    {
        for (int index = 1; ; index++)
        {
            string suffix = index == 1 ? " backup" : " backup " + index;
            string stem = name.Length + suffix.Length > MaxNameLength
                ? name[..(MaxNameLength - suffix.Length)].TrimEnd()
                : name;
            string candidate = stem + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private static Dictionary<string, string> Meaningful(IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values == null) return result;
        foreach ((string key, string value) in values)
        {
            if (!string.IsNullOrEmpty(value)) result[key] = value;
        }
        return result;
    }
}
