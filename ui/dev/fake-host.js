// A stand-in for the C# host so the deck renders in a normal browser (not shipped).
// It answers the same messages with plausible data; nothing is persisted.

const listeners = [];
const deliver = (data) => setTimeout(() => listeners.forEach((listener) => listener({ data })), 30);

const heroes = ["Warden", "Peacekeeper", "Centurion", "Blackprior", "Gryphon", "Conqueror", "Lawbringer", "Gladiator",
  "Warmonger", "Raider", "Berserker", "Highlander", "Jormungandr", "Warlord", "Valkyrie", "Shaman", "Varangian", "Null",
  "Kensei", "Orochi", "Shinobi", "Hitokiri", "Sohei", "Shugoki", "Nobushi", "Aramusha", "Kyoshin", "Tiandi", "Nuxia",
  "Zhanhu", "Jiangjun", "Shaolin", "Juren", "Pirate", "Afeera", "Medjay", "Khatun", "Ocelotl", "Virtuosa"];

const settings = {
  res1: "1920", res2: "1080", Pause: 0, Pause1: 40, Pause2: 120, Pause3: 0, ParryDelay: 60, LegitParryChance: 55,
  CrushingFallbackChance: 50, DeflectFallbackChance: 50, GuardHold: 750, Left: 90, Right: 90, TopDeflect: 110,
  AutoDodgeBind: "LB", OrangeParryBind: "", AutoParryBind: "",
  DodgeL: false, DodgeH: true, Leftdodge: false, Rightdodge: false, Unblockables: true, OrangeLight: false,
  OrangeParry: false, Autoblock: true, Lightbash: false, Parry: true, Crushing: false, Deflect: false,
  Nohero: false, YourHero: true, Legit: true, BulwarkFallback: false, IgnoreOwnOrange: true,
};
heroes.forEach((hero) => { settings[hero] = hero === "Orochi"; });

const host = {
  cloudEmail: "",
  cloudLastSync: "",
  licensed: false,
  profile: "Default",
  profiles: ["Default", "Ranked", "Duels — Orochi"],
  profileDirty: false,
  running: false,
  paused: false,
  orangeParry: false,
  visionOverlay: false,
  anchorScan: true,
  telemetry: false,
  telemetryStarted: 0,
  capturing: "",
  rtSent: 0,
  confirmed: 0,
};

function behavior() {
  const fAction = !settings.Autoblock ? "Inactive" : settings.Parry ? (settings.Legit ? "Mixed parry" : "Parry")
    : settings.Crushing ? "Crushing counter" : settings.Deflect ? "Deflect" : "None";
  const hero = heroes.find((key) => settings[key]) || "None";
  const notices = [];
  if (!settings.Autoblock) notices.push("Enable Auto Block to arm LT reactions.");
  return {
    Hero: hero,
    HeroResponseEnabled: settings.YourHero && hero !== "None",
    HeroMode: settings.YourHero && hero !== "None" ? "MODIFIER ACTIVE" : "INACTIVE",
    FAction: fAction,
    FDetail: fAction === "Mixed parry"
      ? settings.LegitParryChance + "% parry after " + settings.ParryDelay + " ms; otherwise block only."
      : fAction === "Parry" ? "Parry after the flash gate and " + settings.ParryDelay + " ms delay." : "Holding LT keeps the normal guard active.",
    HeroBehavior: hero === "Orochi" ? "Successful deflects follow up with a heavy instead of a light." : "No hero-specific behavior is selected.",
    HeroStatus: hero === "Orochi" ? "Applies when Deflect is the active LT action." : "Inactive.",
    HeroRequirements: "Select the hero, then enable hero response.",
    HeroTimings: "Uses the Deflect delays.",
    Notices: notices,
    DeflectTiming: settings.Deflect ? "Deflect is selected; these delays drive LT dodges." : "Deflect is not selected, so these values do not affect the current LT response.",
    ParryTiming: settings.Parry ? "Parry is the current LT priority; this delay is active." : "Parry is not the current priority, so this delay may be inactive.",
    DodgeTiming: settings.Unblockables ? "Orange response is enabled; these delays are active." : "Orange response is disabled, so these delays do not affect the current reaction.",
  };
}

function status() {
  const t = Date.now() / 1000;
  const live = host.running && !host.paused;
  return {
    version: "2.0.0", build: "Debug", profile: host.profile, profileDirty: host.profileDirty, timingsDirty: false,
    behavior: behavior(), profiles: host.profiles, running: host.running, paused: host.paused, error: "",
    marker: live && Math.sin(t) > -0.6 ? "FOUND" : "MISSING", hold: live && Math.sin(t * 3) > 0.7 ? "DOWN" : "UP",
    indicator: live && Math.sin(t * 2) > 0.8 ? "YES" : "NO", guard: live ? ["LFT", "TOP", "RGT"][Math.floor(t) % 3] : "-",
    flash: "NO",
    lastReaction: host.rtSent
      ? { state: "PARRY CONFIRMED", reason: "Flash on LEFT; RT after 60 ms.", direction: "LEFT", delayMs: 60, confirmation: "CONFIRMED" }
      : { state: "NONE", reason: "", direction: "", delayMs: -1, confirmation: "WAITING" },
    parryCount: host.rtSent, rtSent: host.rtSent, parryAttempts: host.rtSent, parriesConfirmed: host.confirmed,
    parriesUnconfirmed: 0, injected: host.rtSent * 3, mode: "ViGEm", source: "ON", sourceSlot: 0,
    sourceType: "direct-ds4", virtualState: "ON",
    bridge: { Available: true, SourceConnected: true, SourceSlot: -2, SourceType: "direct-ds4", SourceTransport: "hid", OtherXInputControllers: 0 },
    loop: live ? 118 + Math.round(Math.sin(t) * 4) : 0, legit: settings.Legit, orangeParry: host.orangeParry,
    autoDodgeBind: settings.AutoDodgeBind || "UNBOUND", orangeParryBind: settings.OrangeParryBind || "UNBOUND",
    autoParryBind: settings.AutoParryBind || "UNBOUND",
    bindingAutoDodge: host.capturing === "AutoDodge", bindingOrangeParry: host.capturing === "OrangeParry",
    bindingAutoParry: host.capturing === "AutoParry",
    visionOverlay: host.visionOverlay, anchorScan: host.anchorScan,
    cloud: { available: true, signedIn: Boolean(host.cloudEmail), email: host.cloudEmail, busy: false,
      lastSync: host.cloudLastSync, error: "",
      license: host.licensed
        ? { active: true, state: "active", summary: "Lifetime license · 1 PC · key …WEJX" }
        : host.cloudEmail
          ? { active: false, state: "none", summary: "No license on this account. Redeem a license key." }
          : { active: false, state: "unchecked", summary: "A license is needed to start DANBOT. Sign in and redeem your key on the Diagnostics page." } },
    telemetry: {
      recording: host.telemetry, label: "Training",
      durationSeconds: host.telemetry ? Math.round((Date.now() - host.telemetryStarted) / 1000) : 0, failures: 0, dropped: 0,
    },
  };
}

const toast = (message, kind) => deliver({ type: "toast", message, kind });
const sendStatus = () => deliver({ type: "status", status: status() });
const sendSettings = () => deliver({ type: "settings", settings: Object.assign({}, settings) });

function capture(action) {
  host.capturing = host.capturing === action ? "" : action;
  if (host.capturing) {
    setTimeout(() => {
      if (host.capturing !== action) return;
      settings[action + "Bind"] = "RB";
      host.capturing = "";
      host.profileDirty = true;
      sendSettings();
      toast(action + " bound to RB.", "success");
    }, 1800);
  }
}

window.__danbotFakeHost = {
  addEventListener(type, listener) {
    if (type === "message") listeners.push(listener);
  },
  postMessage(message) {
    switch (message.type) {
      case "ready":
        deliver({ type: "init", protocol: 1, settings: Object.assign({}, settings), status: status() });
        break;
      case "settings":
        Object.assign(settings, message.settings);
        host.profileDirty = true;
        sendSettings();
        break;
      case "start":
      case "apply":
        if (message.type === "start") {
          host.running = true;
          host.paused = false;
          setInterval(() => { if (host.running && !host.paused) { host.rtSent += 1; host.confirmed += Math.random() > 0.3 ? 1 : 0; } }, 2500);
        }
        sendSettings();
        deliver({ type: "apply-result", requestId: message.requestId, success: true, message: "Timings applied." });
        toast(message.type === "start" ? "DANBOT is running." : "Timings applied.", "success");
        break;
      case "toggle-pause":
        host.paused = !host.paused;
        toast(host.paused ? "DANBOT paused." : "DANBOT resumed.", "info");
        break;
      case "orange-parry":
        host.orangeParry = !host.orangeParry;
        settings.OrangeParry = host.orangeParry;
        toast(host.orangeParry ? "Orange parry ON" : "Orange parry OFF", host.orangeParry ? "success" : "info");
        break;
      case "vision-overlay":
        host.visionOverlay = !host.visionOverlay;
        toast(host.visionOverlay ? "Vision overlay enabled." : "Vision overlay hidden.", "info");
        break;
      case "anchor-scan":
        host.anchorScan = !host.anchorScan;
        break;
      case "bind-auto-dodge": capture("AutoDodge"); break;
      case "bind-orange-parry": capture("OrangeParry"); break;
      case "bind-auto-parry": capture("AutoParry"); break;
      case "telemetry":
        host.telemetry = !host.telemetry;
        host.telemetryStarted = Date.now();
        toast(host.telemetry ? "Telemetry recording: " + message.label + "." : "Telemetry saved locally.", "info");
        break;
      case "profile-save":
      case "profile-save-as":
        if (message.name && !host.profiles.includes(message.name)) host.profiles.push(message.name);
        if (message.name) host.profile = message.name;
        host.profileDirty = false;
        toast("Profile saved: " + host.profile + ".", "success");
        break;
      case "profile-select":
      case "profile-load":
      case "profile-delete":
        if (message.type === "profile-select") host.profile = message.name;
        if (message.type === "profile-delete") {
          host.profiles = host.profiles.filter((name) => name !== message.name);
          host.profile = "Default";
        }
        host.profileDirty = false;
        sendSettings();
        toast("Profile loaded: " + host.profile + ".", "success");
        break;
      case "scan":
        deliver({ type: "dialog", title: "Screen scan", eyebrow: "DIAGNOSTICS", body: "Marker: FOUND at (912, 388)\nBox: 2\nCombat ROI: 700,400 360x450\nIndicator: none" });
        break;
      case "test":
        toast("Testing Dodge (A) in 3 seconds.", "info");
        break;
      case "resolution":
        toast("Resolution set to " + settings.res1 + " x " + settings.res2 + ".", "success");
        break;
      case "export-telemetry":
        toast("Telemetry ZIP exported.", "success");
        break;
      case "cloud-sign-in":
      case "cloud-sign-up":
        host.cloudEmail = message.email;
        host.cloudLastSync = new Date().toTimeString().slice(0, 5);
        toast("Signed in as " + message.email + ".", "success");
        break;
      case "cloud-sync":
        host.cloudLastSync = new Date().toTimeString().slice(0, 5);
        toast("Cloud profiles are up to date.", "success");
        break;
      case "license-redeem":
        host.licensed = true;
        toast("Lifetime license · 1 PC · key …WEJX", "success");
        break;
      case "cloud-sign-out":
        host.cloudEmail = "";
        host.licensed = false;
        toast("Signed out. Local profiles stay on this PC.", "info");
        break;
    }
    if (message.type !== "ready") sendStatus();
  },
};

setInterval(sendStatus, 200);
