namespace HappyBot;

/// <summary>
/// Owns profile selection, the settings draft, and dirty tracking against a
/// <see cref="ProfileStore"/>. UI-agnostic: operations return an error message
/// (null on success) and the form decides what to display. All members run on
/// the UI thread, like the form code they replace.
/// </summary>
internal sealed class ProfileEditorController
{
    public const string DiscardBeforeLoad = "Discard unsaved profile changes before loading.";
    public const string DiscardBeforeSwitch = "Discard unsaved profile changes before switching.";
    public const string DiscardBeforeDelete = "Discard unsaved profile changes before deleting.";

    private readonly ProfileStore _profiles;

    public ProfileEditorController(ProfileStore profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public Settings EditorSettings { get; private set; } = new();

    public string ActiveProfile { get; private set; } = ProfileStore.DefaultProfileName;

    public bool IsDirty { get; private set; }

    public IReadOnlyList<string> ProfileNames()
    {
        try { return _profiles.ListProfiles(); }
        catch { return new[] { ProfileStore.DefaultProfileName }; }
    }

    /// <summary>Loads the stored active profile (or Default), seeding resolution.</summary>
    public void Initialize(string res1, string res2)
    {
        string startupProfile = "";
        try { startupProfile = _profiles.ReadActiveProfile(); } catch { }
        if (!ProfileNames().Contains(startupProfile, StringComparer.OrdinalIgnoreCase))
            startupProfile = ProfileStore.DefaultProfileName;
        LoadIntoEditor(startupProfile, res1, res2);
    }

    /// <summary>Replaces the draft and marks it dirty (hotkeys, binds, UI edits).</summary>
    public void ReplaceEditor(Settings next)
    {
        EditorSettings = next ?? throw new ArgumentNullException(nameof(next));
        IsDirty = true;
    }

    /// <summary>Reloads the active profile, guarding unsaved changes.</summary>
    public string TryLoadActive(bool discard, bool draftDirty)
    {
        if ((IsDirty || draftDirty) && !discard)
            return DiscardBeforeLoad;
        LoadIntoEditor(ActiveProfile, EditorSettings.Res1, EditorSettings.Res2);
        return null;
    }

    /// <summary>Switches to another profile, guarding unsaved changes.</summary>
    public string Select(string profileName, bool discard, bool draftDirty)
    {
        string normalized;
        try
        {
            normalized = ProfileStore.NormalizeProfileName(profileName);
            if (!ProfileNames().Contains(normalized, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("That profile does not exist.", nameof(profileName));
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if ((IsDirty || draftDirty) && !discard)
            return DiscardBeforeSwitch;
        LoadIntoEditor(normalized, EditorSettings.Res1, EditorSettings.Res2);
        return null;
    }

    public void SaveActive() => SaveProfile(ActiveProfile);

    /// <summary>Saves the draft under a new name and makes it active.</summary>
    public string SaveAs(string profileName)
    {
        try
        {
            // SaveProfile activates the profile and clears the dirty flag.
            SaveProfile(ProfileStore.NormalizeProfileName(profileName));
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public string Delete(string profileName, bool discard, bool draftDirty)
    {
        string normalized;
        try
        {
            normalized = ProfileStore.NormalizeProfileName(profileName);
            if (normalized.Equals(ProfileStore.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Default profile cannot be deleted.");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if ((IsDirty || draftDirty) && !discard)
            return DiscardBeforeDelete;
        _profiles.Delete(normalized);
        if (ActiveProfile.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            LoadIntoEditor(ProfileStore.DefaultProfileName, EditorSettings.Res1, EditorSettings.Res2);
        return null;
    }

    private void LoadIntoEditor(string profileName, string res1, string res2)
    {
        string normalized = ProfileStore.NormalizeProfileName(profileName);
        Settings loaded = new()
        {
            Res1 = res1,
            Res2 = res2
        };
        foreach (string key in SettingsCodec.EditKeys)
        {
            string value = _profiles.Read(normalized, key);
            if (value.Length > 0) SettingsCodec.SetEdit(loaded, key, value);
        }
        foreach (string key in SettingsCodec.CheckKeys)
        {
            string value = _profiles.Read(normalized, key);
            if (value.Length > 0) SettingsCodec.SetCheck(loaded, key, value == "1");
        }

        SettingsCodec.NormalizeHeroSelection(loaded);
        ActiveProfile = normalized;
        EditorSettings = loaded;
        IsDirty = false;
        PersistActiveProfile();
    }

    private void SaveProfile(string profileName)
    {
        string normalized = ProfileStore.NormalizeProfileName(profileName);
        Settings s = EditorSettings;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in SettingsCodec.EditKeys) values[key] = SettingsCodec.GetEdit(s, key);
        foreach (string key in SettingsCodec.CheckKeys) values[key] = SettingsCodec.GetCheck(s, key) ? "1" : "0";
        _profiles.WriteAll(normalized, values);
        ActiveProfile = normalized;
        IsDirty = false;
        PersistActiveProfile();
    }

    private void PersistActiveProfile()
    {
        try
        {
            _profiles.WriteActiveProfile(ActiveProfile);
        }
        catch
        {
            // Profile selection should remain usable even if metadata cannot be written.
        }
    }
}
