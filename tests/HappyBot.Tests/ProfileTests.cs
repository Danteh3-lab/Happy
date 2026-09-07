using System.Drawing;
using System.Text.Json;
using HappyBot;
using HappyBot.Automation;
using HappyBot.Combat;
using HappyBot.Infrastructure.Input;
using HappyBot.Vision;

static partial class Program
{
    private static void ProfileStoreRoundTripsAndProtectsPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "HappyBot.ProfileTests", Guid.NewGuid().ToString("N"));
        string readOnlyFallbackPath = Path.Combine(root, "legacy", "Profiles", "Read Only Legacy.ini");
        Directory.CreateDirectory(root);
        try
        {
            var store = new ProfileStore(root);
            Require(store.ListProfiles().SequenceEqual(new[] { ProfileStore.DefaultProfileName }),
                "a new profile store should expose Default");

            store.Write(ProfileStore.DefaultProfileName, "Parry", "1");
            store.Write(ProfileStore.DefaultProfileName, "HoldButton", "LT");
            store.Write("Side Guard", "Left", "17");
            store.Write("Side Guard", "Right", "23");
            Require(store.Read(ProfileStore.DefaultProfileName, "Parry") == "1",
                "Default should use the legacy Config.ini path");
            store.WriteAll(ProfileStore.DefaultProfileName, new Dictionary<string, string>
            {
                ["Parry"] = "0"
            });
            Require(store.Read(ProfileStore.DefaultProfileName, "Parry") == "0" && store.Read(ProfileStore.DefaultProfileName, "HoldButton") == "LT",
                "atomic profile saves should update known keys without dropping unrelated settings");
            Require(store.Read("Side Guard", "Left") == "17" && store.Read("Side Guard", "Right") == "23",
                "named profiles should round-trip independent values");
            Require(store.ListProfiles().SequenceEqual(new[] { "Default", "Side Guard" }),
                "profiles should list Default first and named profiles alphabetically");

            bool traversalRejected = false;
            try { store.Write("..\\escape", "Value", "1"); }
            catch (ArgumentException) { traversalRejected = true; }
            Require(traversalRejected, "profile traversal names must be rejected");

            bool defaultDeleteRejected = false;
            try { store.Delete(ProfileStore.DefaultProfileName); }
            catch (InvalidOperationException) { defaultDeleteRejected = true; }
            Require(defaultDeleteRejected, "Default must not be deletable");
            store.Delete("Side Guard");
            Require(store.ListProfiles().SequenceEqual(new[] { ProfileStore.DefaultProfileName }),
                "deleted profiles should disappear from the list");

            string legacyRoot = Path.Combine(root, "legacy");
            string stableRoot = Path.Combine(root, "stable");
            var legacyStore = new ProfileStore(legacyRoot);
            legacyStore.Write(ProfileStore.DefaultProfileName, "Parry", "1");
            legacyStore.Write("Legacy Profile", "Left", "31");
            var stableStore = new ProfileStore(stableRoot, legacyRoot);
            Require(stableStore.Read(ProfileStore.DefaultProfileName, "Parry") == "1" &&
                    stableStore.Read("Legacy Profile", "Left") == "31" &&
                    stableStore.ListProfiles().SequenceEqual(new[] { "Default", "Legacy Profile" }),
                "stable stores should read profiles from the legacy executable directory during migration");
            stableStore.WriteAll("Legacy Profile", new Dictionary<string, string> { ["Left"] = "44" });
            Require(File.Exists(Path.Combine(stableRoot, "Profiles", "Legacy Profile.ini")) &&
                    stableStore.Read("Legacy Profile", "Left") == "44",
                "saving a legacy profile should materialize it in the stable store");
            stableStore.WriteActiveProfile("Legacy Profile");
            Require(stableStore.ReadActiveProfile() == "Legacy Profile",
                "the active profile should persist and round-trip");
            stableStore.WriteActiveProfile(ProfileStore.DefaultProfileName);
            Require(stableStore.ReadActiveProfile() == ProfileStore.DefaultProfileName,
                "the active profile metadata should support Default");

            legacyStore.Write("Read Only Legacy", "Left", "55");
            File.SetAttributes(readOnlyFallbackPath, FileAttributes.ReadOnly);
            stableStore.Delete("Read Only Legacy");
            Require(!stableStore.ListProfiles().Contains("Read Only Legacy", StringComparer.OrdinalIgnoreCase),
                "a deleted read-only fallback profile must stay hidden by its tombstone");
            var restartedStore = new ProfileStore(stableRoot, legacyRoot);
            Require(!restartedStore.ListProfiles().Contains("Read Only Legacy", StringComparer.OrdinalIgnoreCase),
                "a fallback deletion tombstone must persist across store reloads");
        }
        finally
        {
            if (File.Exists(readOnlyFallbackPath)) File.SetAttributes(readOnlyFallbackPath, FileAttributes.Normal);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void SettingsCodecApplyJsonClampsAndValidates()
    {
        using var document = JsonDocument.Parse(
            """{"Pause":-5,"Pause3":20000,"GuardHold":5,"LegitParryChance":150,"Left":42,"Parry":true,"UnknownKey":true}""");
        Settings merged = SettingsCodec.ApplyJson(new Settings(), document.RootElement);
        Require(merged.Pause == 0 && merged.Pause3 == 10000 && merged.GuardHold == 60 &&
            merged.LegitParryChance == 100 && merged.Left == 42 && merged.Parry,
            "JSON settings must clamp delays, chances, and guard hold while applying switches");
        Require(!merged.Chars.ContainsKey("UnknownKey"),
            "unknown JSON keys must not pollute the hero map");
    }

    private static void SettingsCodecDodgeSidesStayExclusive()
    {
        var settings = new Settings();
        SettingsCodec.SetCheck(settings, "Leftdodge", true);
        SettingsCodec.SetCheck(settings, "Rightdodge", true);
        Require(settings.Rightdodge && !settings.Leftdodge,
            "enabling one dodge side must clear the other");
    }

    private static void SettingsCodecHeroSelectionKeepsFirst()
    {
        var settings = new Settings();
        SettingsCodec.SetCheck(settings, "Gryphon", true);
        SettingsCodec.SetCheck(settings, "Warden", true);
        SettingsCodec.NormalizeHeroSelection(settings);
        Require(settings.Ch("Warden") && !settings.Ch("Gryphon"),
            "hero selection must keep the first hero in canonical order");
    }

    private static void SettingsCodecTryParseResolution()
    {
        Require(SettingsCodec.TryParseResolution(new Settings { Res1 = "1920", Res2 = "1080" }, out int w, out int h) &&
            w == 1920 && h == 1080, "a valid landscape resolution must parse");
        Require(!SettingsCodec.TryParseResolution(new Settings { Res1 = "1080", Res2 = "1920" }, out _, out _),
            "a portrait resolution must be rejected");
        Require(!SettingsCodec.TryParseResolution(new Settings { Res1 = "abc", Res2 = "1080" }, out _, out _),
            "a non-numeric resolution must be rejected");
    }

    private static void SettingsCodecEditRoundTrip()
    {
        var settings = new Settings { Pause = 80, GuardHold = 750, AutoDodgeBind = " A " };
        Require(SettingsCodec.GetEdit(settings, "Pause") == "80" &&
            SettingsCodec.GetEdit(settings, "AutoDodgeBind") == " A ",
            "edit getters must expose raw values");

        var loaded = new Settings();
        SettingsCodec.SetEdit(loaded, "Pause", "80");
        SettingsCodec.SetEdit(loaded, "GuardHold", "5");
        SettingsCodec.SetEdit(loaded, "AutoDodgeBind", " A ");
        Require(loaded.Pause == 80 && loaded.GuardHold == 60 && loaded.AutoDodgeBind == "A",
            "edit setters must clamp and trim like the live form");
    }

    private static void ProfileEditorDirtyLifecycle()
    {
        string root = Path.Combine(Path.GetTempPath(), "HappyBot.EditorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var editor = new ProfileEditorController(new ProfileStore(root));
            editor.Initialize("800", "600");
            Require(editor.ActiveProfile == ProfileStore.DefaultProfileName && !editor.IsDirty,
                "initialization must load the default profile clean");
            Require(editor.EditorSettings.Res1 == "800" && editor.EditorSettings.Res2 == "600",
                "initialization must seed the resolution, which profiles never persist");

            Settings dirty = editor.EditorSettings.Clone();
            dirty.Pause = 80;
            editor.ReplaceEditor(dirty);
            Require(editor.IsDirty, "replacing the draft must mark it dirty");
            Require(editor.TryLoadActive(discard: false, draftDirty: false) ==
                ProfileEditorController.DiscardBeforeLoad, "a dirty draft must block loading");
            Require(editor.TryLoadActive(discard: true, draftDirty: false) == null && !editor.IsDirty,
                "discarding must reload the saved profile clean");

            Require(editor.SaveAs("Bad/Name") != null, "invalid profile names must be rejected");
            Require(editor.SaveAs("Side Guard") == null && editor.ActiveProfile == "Side Guard",
                "save-as must activate the new profile");
            Require(editor.Select("Missing", discard: true, draftDirty: false) != null,
                "selecting an unknown profile must fail");
            Require(editor.Select("Side Guard", discard: true, draftDirty: false) == null,
                "selecting an existing profile must succeed");
            Require(editor.Delete(ProfileStore.DefaultProfileName, discard: true, draftDirty: false) != null,
                "the Default profile must not be deletable");
            Require(editor.Delete("Side Guard", discard: true, draftDirty: false) == null &&
                editor.ActiveProfile == ProfileStore.DefaultProfileName,
                "deleting the active profile must fall back to Default");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
