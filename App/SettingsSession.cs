namespace HappyBot.App;

/// <summary>
/// The single home for the draft-versus-live settings rule.
///
/// Invariant: the bot's live switches always equal the draft's switches, and
/// <see cref="BotCore.OrangeParry"/> always equals the draft's OrangeParry.
/// Numeric timings only reach the bot through <see cref="CommitAll"/>, except
/// for keys an edit names explicitly (F1's orange delay toggle).
///
/// UI-thread only, like the <see cref="ProfileEditorController"/> it wraps.
/// </summary>
internal sealed class SettingsSession
{
    private readonly ProfileEditorController _editor;
    private readonly BotCore _bot;

    public SettingsSession(ProfileEditorController editor, BotCore bot)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
    }

    public Settings Draft => _editor.EditorSettings;

    public string ActiveProfile => _editor.ActiveProfile;

    public bool IsDirty => _editor.IsDirty;

    public IReadOnlyList<string> ProfileNames() => _editor.ProfileNames();

    /// <summary>Loads the startup profile and pushes all of it live.</summary>
    public void Initialize(string res1, string res2)
    {
        _bot.UpdateSettings(s =>
        {
            s.Res1 = res1;
            s.Res2 = res2;
        });
        _editor.Initialize(res1, res2);
        CommitAll();
    }

    public void SyncLiveSwitches()
    {
        Settings draft = _editor.EditorSettings;
        _bot.UpdateSettings(s => s.CopyLiveSwitchesFrom(draft));
        _bot.OrangeParry = draft.OrangeParry;
    }

    /// <summary>Replaces the whole draft (a page `settings` message).</summary>
    public void ReplaceDraft(Settings next)
    {
        _editor.ReplaceEditor(next);
        SyncLiveSwitches();
    }

    /// <summary>
    /// Edits a copy of the draft, keeps switches live, and pushes only the named
    /// timing keys live. Every other timing stays pending until Apply.
    /// </summary>
    public Settings EditDraft(Action<Settings> edit, params string[] commitTimingKeys)
    {
        ArgumentNullException.ThrowIfNull(edit);
        Settings next = _editor.EditorSettings.Clone();
        edit(next);
        _editor.ReplaceEditor(next);
        SyncLiveSwitches();

        if (commitTimingKeys is { Length: > 0 })
        {
            var fields = Settings.TimingFields
                .Where(f => commitTimingKeys.Contains(f.Key, StringComparer.Ordinal))
                .ToArray();
            if (fields.Length != commitTimingKeys.Length)
                throw new ArgumentException("Unknown timing key.", nameof(commitTimingKeys));
            _bot.UpdateSettings(live =>
            {
                foreach (var field in fields) field.Set(live, field.Get(next));
            });
        }
        return next;
    }

    /// <summary>Pushes the whole draft live, including timings and resolution.</summary>
    public void CommitAll()
    {
        Settings snapshot = _editor.EditorSettings.Clone();
        SettingsCodec.ApplyPeacekeeperRuntimeOverride(snapshot);
        _bot.UpdateSettings(s => s.CopyFrom(snapshot));
        _bot.OrangeParry = _editor.EditorSettings.OrangeParry;
    }

    public bool HasUnappliedTimings()
    {
        Settings draft = _editor.EditorSettings.Clone();
        SettingsCodec.ApplyPeacekeeperRuntimeOverride(draft);
        return !draft.TimingsEqual(_bot.S);
    }

    public bool TryReadResolution(out int width, out int height) =>
        SettingsCodec.TryParseResolution(_editor.EditorSettings, out width, out height);

    public string TryLoadActive(bool discard, bool draftDirty) =>
        SyncOnSuccess(_editor.TryLoadActive(discard, draftDirty));

    public string Select(string profileName, bool discard, bool draftDirty) =>
        SyncOnSuccess(_editor.Select(profileName, discard, draftDirty));

    public string Delete(string profileName, bool discard, bool draftDirty) =>
        SyncOnSuccess(_editor.Delete(profileName, discard, draftDirty));

    public void SaveActive() => _editor.SaveActive();

    public string SaveAs(string profileName) => _editor.SaveAs(profileName);

    private string SyncOnSuccess(string error)
    {
        if (error == null) SyncLiveSwitches();
        return error;
    }
}
