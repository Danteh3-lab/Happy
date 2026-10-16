using System.Reflection;
using HappyBot;
using HappyBot.App;
using HappyBot.Combat;

static partial class Program
{
    private static void SettingsSessionKeepsLiveSwitchesEqualToDraft()
    {
        string root = Path.Combine(Path.GetTempPath(), "HappyBot.SessionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bot = new BotCore(new FakeInputGateway(), new FixedRollSource(0), new FixedOrangeDirectionSource(CombatDirection.Top));
        try
        {
            var session = new SettingsSession(new ProfileEditorController(new ProfileStore(root)), bot);
            session.Initialize("1920", "1080");
            RequireLiveMatchesDraft(bot, session, "after initialize");
            Require(bot.S.Res1 == "1920" && !session.HasUnappliedTimings(), "initialize commits everything");

            Settings next = session.Draft.Clone();
            next.Autoblock = true;
            next.OrangeParry = true;
            next.Chars["Peacekeeper"] = true;
            next.GuardHold = 900;
            session.ReplaceDraft(next);
            RequireLiveMatchesDraft(bot, session, "after ReplaceDraft");
            Require(bot.S.GuardHold != 900 && session.HasUnappliedTimings(), "ReplaceDraft leaves timings pending");

            session.EditDraft(s => ControllerToggleBindings.Assign(s, ControllerToggleAction.AutoParry, "LB"));
            RequireLiveMatchesDraft(bot, session, "after a bind edit");

            session.EditDraft(s => { s.Pause = 80; s.Left = 300; }, "Pause");
            Require(bot.S.Pause == 80 && bot.S.Left != 300, "EditDraft commits only the named timing keys");

            bool threw = false;
            try { session.EditDraft(_ => { }, "NotATiming"); }
            catch (ArgumentException) { threw = true; }
            Require(threw, "unknown timing keys must be rejected");

            session.CommitAll();
            Require(!session.HasUnappliedTimings() && bot.S.GuardHold == 900, "CommitAll applies every timing");
            Require(bot.S.Left == SettingsCodec.PeacekeeperDeflectDelayMs && session.Draft.Left == 300,
                "the Peacekeeper override applies to the live copy only");

            Require(session.SaveAs("Session Test") == null, "save-as succeeds");
            Settings dirty = session.Draft.Clone();
            dirty.Autoblock = false;
            dirty.OrangeParry = false;
            session.ReplaceDraft(dirty);
            Require(session.Select(ProfileStore.DefaultProfileName, discard: false, draftDirty: false) != null,
                "a dirty draft blocks switching profiles");
            Require(session.Select("Session Test", discard: true, draftDirty: false) == null, "discarding switches profiles");
            RequireLiveMatchesDraft(bot, session, "after Select");
            Require(bot.S.Autoblock && bot.OrangeParry, "selecting a profile pushes its switches live");

            Require(session.Delete("Session Test", discard: true, draftDirty: false) == null, "delete succeeds");
            RequireLiveMatchesDraft(bot, session, "after Delete");
        }
        finally
        {
            bot.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void TimingFieldsCoverEveryNumericSetting()
    {
        var numeric = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(int))
            .Select(f => f.Name)
            .ToHashSet();
        var timing = Settings.TimingFields.Select(f => f.Key).ToHashSet();
        Require(numeric.SetEquals(timing),
            "Settings int fields and TimingFields differ: " + string.Join(",", numeric.Except(timing).Concat(timing.Except(numeric))));

        var a = new Settings();
        var b = new Settings();
        foreach (var field in Settings.TimingFields)
        {
            field.Set(b, field.Get(a) + 7);
            Require(!a.TimingsEqual(b) && field.Get(b) == field.Get(a) + 7, field.Key + " must round-trip through the table");
            a.CopyTimingsFrom(b);
            Require(a.TimingsEqual(b), field.Key + " must copy");
        }
    }

    private static void RequireLiveMatchesDraft(BotCore bot, SettingsSession session, string when)
    {
        Settings live = bot.S;
        Settings draft = session.Draft;
        foreach (string key in SettingsCodec.CheckKeys)
        {
            Require(SettingsCodec.GetCheck(live, key) == SettingsCodec.GetCheck(draft, key), key + " differs " + when);
        }
        Require(live.AutoDodgeBind == draft.AutoDodgeBind && live.OrangeParryBind == draft.OrangeParryBind &&
            live.AutoParryBind == draft.AutoParryBind, "binds differ " + when);
        Require(bot.OrangeParry == draft.OrangeParry, "BotCore.OrangeParry differs " + when);
    }
}
