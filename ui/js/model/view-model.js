// Derives every display string and state flag from settings + status.
// Pure: the renderer only copies these values into [data-bind*] elements.
import { behaviorValue, resolutionReady, selectedHero, heroResponseEnabled } from "./settings-model.js";
import { heroLabel } from "./hero-catalog.js";

const upper = (value, fallback) => String(value ?? fallback).toUpperCase();

function runState(status) {
  if (status.error) return "error";
  if (status.running === true) return status.paused === true ? "paused" : "live";
  return "standby";
}

const RUN_TEXT = {
  standby: {
    label: "STANDBY",
    title: "Ready to launch",
    copy: "Configure your reactions, then start the bot before returning to the game.",
  },
  live: {
    label: "LIVE",
    title: "Bot is live",
    copy: "The reaction loop is running. Return to the game when your controller is ready.",
  },
  paused: {
    label: "PAUSED",
    title: "Bot is paused",
    copy: "Reaction delivery is paused. Resume when your source controller is ready.",
  },
  error: { label: "ERROR", title: "Runtime error", copy: "" },
};

function confirmationState(confirmation) {
  switch (confirmation) {
    case "CONFIRMED": return "ok";
    case "INPUT SENT":
    case "PENDING": return "warn";
    case "NOT DELIVERED":
    case "UNCONFIRMED": return "bad";
    default: return "idle";
  }
}

function heroModeState(mode) {
  if (["ENABLED", "OVERRIDE ACTIVE", "MODIFIER ACTIVE"].includes(mode)) return "ok";
  if (mode === "TIMING ONLY") return "warn";
  return "idle";
}

/**
 * @param settings the page's settings draft
 * @param status   the effective status (see draft-state.effectiveStatus)
 * @param flags    { applyPending }
 */
export function deriveView(settings, status, flags) {
  const s = settings || {};
  const st = status || {};
  const applyPending = Boolean(flags && flags.applyPending);
  const vm = {};

  // Run state
  const run = runState(st);
  const text = RUN_TEXT[run];
  const running = st.running === true;
  const paused = st.paused === true;
  vm.runState = run;
  vm.runLabel = text.label;
  vm.runTitle = text.title;
  vm.runCopy = run === "error" ? String(st.error) : text.copy;
  // The host enforces the license too; an older payload without one is not blocked here.
  const license = (st.cloud && st.cloud.license) || {};
  const licenseBlocked = license.active === false;
  vm.startLabel = running ? (paused ? "Bot paused" : "Bot running") : "Start bot";
  vm.startDisabled = running || applyPending || licenseBlocked;
  vm.pauseLabel = paused ? "Resume" : "Pause";
  vm.pauseDisabled = !running;
  vm.startNote = !running && st.timingsDirty === true;

  // Profile + timing commit state
  const profile = String(st.profile || "Default");
  const profileDirty = st.profileDirty === true;
  const timingsDirty = st.timingsDirty === true;
  vm.profileName = profile;
  vm.profileState = profileDirty ? "warn" : "ok";
  vm.profileStateLabel = profileDirty ? "Profile not saved" : "Profile saved";
  vm.timingState = applyPending ? "busy" : timingsDirty ? "warn" : "ok";
  vm.timingStateLabel = applyPending ? "Applying timings…" : timingsDirty ? "Timings not applied" : "Timings applied";
  vm.applyLabel = applyPending ? "Applying…" : "Apply timings";
  vm.applyDisabled = applyPending || !timingsDirty;
  vm.saveDisabled = !profileDirty;

  // Readiness
  const virtualOn = upper(st.virtualState, "OFF") === "ON";
  vm.readyInputState = virtualOn ? "ok" : "bad";
  vm.readyInputDetail = virtualOn ? "Ready to start" : "Unavailable — reconnect ViGEm";
  const resReady = resolutionReady(s);
  vm.readyResState = resReady ? "ok" : "bad";
  vm.readyResDetail = resReady ? s.res1 + " × " + s.res2 : "Set a valid resolution";
  vm.readyProfileState = profileDirty ? "warn" : "ok";
  vm.readyProfileDetail = profile + (profileDirty ? " · unsaved edits" : " · saved");
  const licenseLeft = String(license.remaining || "");
  const licenseOffline = String(license.confirmWithin || "");
  vm.readyLicenseState = licenseBlocked ? "bad" : license.expiring === true ? "warn" : "ok";
  vm.readyLicenseDetail = licenseBlocked
    ? String(license.summary || "No license")
    : license.active === true
      ? [licenseLeft ? "Licensed · " + licenseLeft : "Lifetime license", licenseOffline].filter(Boolean).join(" · ")
      : "Active";
  vm.readyFixLicense = licenseBlocked;
  vm.readyFixInput = !virtualOn;
  vm.readyFixRes = !resReady;

  // Active behavior
  const behavior = st.behavior;
  const hero = behaviorValue(behavior, "hero", "None");
  vm.behaviorHero = heroLabel(hero);
  vm.fAction = behaviorValue(behavior, "fAction", "None");
  vm.fDetail = behaviorValue(behavior, "fDetail", "No reaction is configured.");
  const notices = behaviorValue(behavior, "notices", []);
  vm.notices = Array.isArray(notices) ? notices.map(String) : [];
  vm.fActionState = vm.fAction === "None" || vm.fAction === "Inactive" ? "idle" : "ok";

  // Hero page
  const selected = selectedHero(s);
  vm.heroSelected = heroLabel(selected);
  vm.heroDetailTitle = hero === "None" ? "No hero selected" : heroLabel(hero);
  vm.heroDetailBehavior = behaviorValue(behavior, "heroBehavior", "No hero-specific behavior is selected.");
  vm.heroDetailStatus = behaviorValue(behavior, "heroStatus", "Inactive.");
  vm.heroDetailRequirements = behaviorValue(behavior, "heroRequirements", "Select a hero.");
  vm.heroDetailTimings = behaviorValue(behavior, "heroTimings", "No hero timing applies.");
  const enabled = behaviorValue(behavior, "heroResponseEnabled", false) === true;
  vm.heroMode = behaviorValue(behavior, "heroMode", enabled ? "ENABLED" : "INACTIVE");
  vm.heroModeState = heroModeState(vm.heroMode);
  vm.heroResponseNote = selected === "Peacekeeper"
    ? "Optional: side deflect timing is forced to 100 ms when timings are applied."
    : selected === ""
      ? "Choose a hero first."
      : "Generic Parry, Crushing, and Deflect can still take priority.";
  vm.heroResponseOn = heroResponseEnabled(s);

  // Timing notes
  const pending = "Timing eligibility is reported after the current settings are acknowledged.";
  vm.peacekeeper = selected === "Peacekeeper";
  vm.deflectAffects = behaviorValue(behavior, "deflectTiming", "") || pending;
  vm.parryAffects = behaviorValue(behavior, "parryTiming", "") || pending;
  vm.dodgeAffects = behaviorValue(behavior, "dodgeTiming", "") || pending;
  vm.timingContext = vm.peacekeeper
    ? "Peacekeeper is selected: side deflect delays are forced to 100 ms when applied; the Top value remains editable."
    : "Timing values are edited locally and stay pending until Apply timings succeeds.";

  // Live signal
  const marker = upper(st.marker, "MISSING");
  const hold = upper(st.hold, "UP");
  const indicator = upper(st.indicator, "NO");
  const guard = String(st.guard || "-");
  const loop = Number(st.loop || 0);
  vm.marker = marker;
  vm.markerState = marker === "FOUND" ? "ok" : "bad";
  vm.hold = hold;
  vm.holdState = hold === "DOWN" ? "ok" : "idle";
  vm.indicator = indicator;
  vm.indicatorState = indicator === "YES" ? "warn" : "idle";
  vm.guard = guard;
  vm.guardState = guard !== "-" && guard !== "UNKNOWN" ? "ok" : "idle";
  vm.loop = loop + " Hz";
  vm.loopState = loop > 0 ? "ok" : "idle";

  // Input bridge
  const source = upper(st.source, "OFF");
  const slot = Number(st.sourceSlot);
  const mode = String(st.mode || "ViGEm");
  const bridge = st.bridge || {};
  vm.mode = mode;
  vm.modeState = mode.toLowerCase() === "vigem" ? "ok" : "warn";
  vm.source = source === "ON" && slot >= 0 ? "ON / " + slot : source;
  vm.sourceState = source === "ON" ? "ok" : "bad";
  vm.virtual = virtualOn ? "ON" : "OFF";
  vm.virtualState = virtualOn ? "ok" : "bad";
  vm.sourceType = String(st.sourceType || bridge.SourceType || "-");
  vm.sourceTransport = String(bridge.SourceTransport || "-");
  const others = Number(bridge.OtherXInputControllers || 0);
  vm.otherXInputWarning = others > 0;
  vm.otherXInput = String(others);
  vm.sidebarInput = mode + " / " + (source === "ON" ? "source " + slot : "idle");
  vm.sidebarInputState = virtualOn ? "ok" : "bad";
  const version = String(st.version || "2.0.0");
  vm.version = "v" + version + (st.build ? " · " + st.build : "");

  // Counters
  const rtSent = st.rtSent ?? st.parryAttempts ?? st.parryCount ?? 0;
  vm.rtSent = String(rtSent);
  vm.parriesConfirmed = String(st.parriesConfirmed || 0);
  vm.parriesUnconfirmed = String(st.parriesUnconfirmed || 0);
  vm.injected = String(st.injected || 0);

  // Last reaction
  const reaction = st.lastReaction || {};
  const reactionState = String(reaction.state || "NONE");
  const hasReaction = reactionState !== "NONE";
  const confirmation = String(reaction.confirmation || (hasReaction ? "OBSERVED" : "WAITING"));
  vm.lastState = hasReaction ? reactionState : "No reaction yet";
  vm.lastConfirmation = confirmation;
  vm.lastConfirmationState = confirmationState(confirmation);
  vm.lastDirection = String(reaction.direction || "-") || "-";
  vm.lastDelay = Number(reaction.delayMs) >= 0 ? Number(reaction.delayMs) + " ms" : "n/a";
  vm.lastReason = String(reaction.reason || "Start the bot to see why the next reaction was sent or skipped.");

  // Toggles owned by the host
  vm.orangeParry = st.orangeParry === true;
  vm.orangeParryLabel = vm.orangeParry ? "ON" : "OFF";
  vm.visionOverlay = st.visionOverlay === true;
  vm.visionOverlayLabel = vm.visionOverlay ? "ON" : "OFF";
  vm.anchorScan = st.anchorScan !== false;
  vm.anchorScanLabel = vm.anchorScan ? "ON" : "OFF";

  // Telemetry
  const telemetry = st.telemetry || {};
  vm.telemetryOn = telemetry.recording === true;
  vm.telemetryLabel = vm.telemetryOn ? "Stop recording" : "Start recording";
  vm.telemetryState = vm.telemetryOn ? "ok" : "idle";
  vm.telemetryStatus = vm.telemetryOn
    ? "Recording " + (telemetry.label || "Other") + " · " + Number(telemetry.durationSeconds || 0) + "s · " +
      Number(telemetry.failures || 0) + " failures · " + Number(telemetry.dropped || 0) + " dropped"
    : "Not recording";

  // Cloud profiles
  const cloud = st.cloud || {};
  const signedIn = cloud.signedIn === true;
  vm.cloudSignedIn = signedIn;
  vm.cloudSignedOut = !signedIn;
  vm.cloudBusy = cloud.busy === true || cloud.available === false;
  vm.cloudEmail = String(cloud.email || "-");
  vm.cloudLastSync = cloud.lastSync ? "Last synced " + cloud.lastSync : "Not synced yet";
  vm.cloudError = String(cloud.error || "");
  vm.cloudErrorShown = vm.cloudError.length > 0;
  vm.cloudBadge = cloud.available === false ? "Unavailable" : cloud.busy ? "Syncing" : signedIn ? "Signed in" : "Signed out";
  vm.cloudState = cloud.busy ? "busy" : vm.cloudErrorShown ? "warn" : signedIn ? "ok" : "idle";
  vm.licenseSummary = [String(license.summary || (license.active ? "License active" : "Checking license")),
    licenseLeft, licenseOffline].filter(Boolean).join(" · ");
  vm.licenseState = license.active === true && license.expiring !== true ? "ok" : "warn";
  vm.licenseNeedsKey = signedIn && license.active !== true && license.state !== "unchecked";

  // Controller binds
  for (const [key, bindKey, captureKey] of [
    ["AutoDodge", "autoDodgeBind", "bindingAutoDodge"],
    ["OrangeParry", "orangeParryBind", "bindingOrangeParry"],
    ["AutoParry", "autoParryBind", "bindingAutoParry"],
  ]) {
    const capturing = st[captureKey] === true;
    const binding = String(st[bindKey] || "UNBOUND");
    vm["bind" + key] = capturing ? "Press a button…" : binding;
    vm["bind" + key + "Capturing"] = capturing;
    vm["bind" + key + "State"] = capturing ? "busy" : binding === "UNBOUND" ? "idle" : "ok";
  }

  return vm;
}
