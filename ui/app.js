(function () {
  "use strict";

  const bridge = window.chrome && window.chrome.webview;
  const state = { settings: {}, status: {} };
  const applyRevision = window.HappyApplyRevision.create();
  const heroGroups = [
    { label: "Knights", keys: ["Warden", "Peacekeeper", "Centurion", "Blackprior", "Gryphon", "Conqueror", "Lawbringer", "Gladiator", "Warmonger"] },
    { label: "Vikings", keys: ["Raider", "Berserker", "Highlander", "Jormungandr", "Warlord", "Valkyrie", "Shaman", "Varangian", "Null"] },
    { label: "Samurai", keys: ["Kensei", "Orochi", "Shinobi", "Hitokiri", "Sohei", "Shugoki", "Nobushi", "Aramusha", "Kyoshin"] },
    { label: "Wu Lin", keys: ["Tiandi", "Nuxia", "Zhanhu", "Jiangjun", "Shaolin", "Juren"] },
    { label: "Outlanders", keys: ["Pirate", "Afeera", "Medjay", "Khatun", "Ocelotl", "Virtuosa"] },
  ];
  const heroKeys = heroGroups.flatMap((group) => group.keys);
  const heroLabels = { Blackprior: "Black Prior", Jiangjun: "Jiang Jun" };
  let hydrating = false;
  let profileHighlighted = -1;
  let profileRenderedNames = [];
  let profileRenderedActive = "";
  let profileDraftDirty = false;
  let profileAwaitingClean = false;
  let profileLoadPending = false;
  let profileTypeahead = "";
  let profileTypeaheadTimer = 0;
  let heroHighlighted = 0;
  let heroTypeahead = "";
  let heroTypeaheadTimer = 0;
  let timingDraftDirty = false;
  let applyPending = false;
  let applyRequestId = "";
  let modalCallback = null;
  let modalReturnFocus = null;

  const $ = (selector) => document.querySelector(selector);
  const $$ = (selector) => Array.from(document.querySelectorAll(selector));

  function post(type, extra) {
    if (!bridge) return;
    bridge.postMessage(Object.assign({ type }, extra || {}));
  }

  function sendSettings() {
    post("settings", { settings: Object.assign({}, state.settings) });
  }

  function sendSettingsThen(type, extra) {
    sendSettings();
    post(type, extra);
  }

  function applySettings(settings) {
    state.settings = Object.assign({}, state.settings, settings || {});
    hydrating = true;
    $$('[data-setting]').forEach((control) => {
      const value = state.settings[control.dataset.setting];
      if (value === undefined) return;
      if (control.type === "checkbox") {
        control.checked = control.dataset.setting === "YourHero"
          ? Boolean(value) && state.settings.Nohero !== true
          : Boolean(value);
      }
      else control.value = value;
    });
    hydrating = false;
    syncHeroControls();
    syncLegitChanceControl();
  }

  function beginAcknowledgedApply(action) {
    applyPending = true;
    applyRequestId = Date.now().toString(36) + Math.random().toString(36).slice(2);
    applyRevision.begin(applyRequestId);
    renderApplyState(true);
    sendSettings();
    post(action, { requestId: applyRequestId });
  }

  function heroOptions() {
    return $$("#hero-select-menu [role=option]");
  }

  function renderHeroPicker() {
    const menu = $("#hero-select-menu");
    if (!menu || menu.dataset.rendered === "true") return;
    menu.replaceChildren();

    const none = document.createElement("button");
    none.type = "button";
    none.className = "hero-select-option";
    none.id = "hero-option-none";
    none.dataset.hero = "";
    none.setAttribute("role", "option");
    none.textContent = "None";
    none.addEventListener("pointerenter", () => highlightHero(0, false));
    none.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      chooseHero("");
    });
    menu.appendChild(none);

    let optionIndex = 1;
    heroGroups.forEach((group) => {
      const groupRoot = document.createElement("div");
      groupRoot.className = "hero-select-group";
      groupRoot.setAttribute("role", "group");
      groupRoot.setAttribute("aria-label", group.label);

      const groupLabel = document.createElement("div");
      groupLabel.className = "hero-select-group-label";
      groupLabel.setAttribute("aria-hidden", "true");
      groupLabel.textContent = group.label;
      groupRoot.appendChild(groupLabel);

      group.keys.forEach((key) => {
        const option = document.createElement("button");
        option.type = "button";
        option.className = "hero-select-option";
        option.id = "hero-option-" + key.toLowerCase();
        option.dataset.hero = key;
        option.setAttribute("role", "option");
        option.textContent = heroLabels[key] || key;
        const index = optionIndex;
        option.addEventListener("pointerenter", () => highlightHero(index, false));
        option.addEventListener("click", (event) => {
          event.preventDefault();
          event.stopPropagation();
          chooseHero(key);
        });
        groupRoot.appendChild(option);
        optionIndex += 1;
      });
      menu.appendChild(groupRoot);
    });
    menu.dataset.rendered = "true";
    highlightHero(heroHighlighted, false);
  }

  function highlightHero(index, scroll) {
    const options = heroOptions();
    if (!options.length) {
      heroHighlighted = -1;
      return;
    }
    heroHighlighted = Math.max(0, Math.min(index, options.length - 1));
    options.forEach((option, optionIndex) => {
      const selected = option.dataset.hero === selectedHero();
      option.classList.toggle("highlighted", optionIndex === heroHighlighted);
      option.setAttribute("aria-selected", selected ? "true" : "false");
    });
    const menu = $("#hero-select-menu");
    if (menu) {
      const active = options[heroHighlighted];
      menu.setAttribute("aria-activedescendant", active.id || "");
      if (scroll && active) active.scrollIntoView({ block: "nearest" });
    }
  }

  function positionHeroMenu() {
    const root = $("#hero-select");
    const trigger = $("#hero-select-trigger");
    if (!root || !trigger) return;
    const rect = trigger.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom;
    root.classList.toggle("open-up", spaceBelow < 320 && rect.top > spaceBelow);
  }

  function setHeroOpen(open, focusMenu) {
    const root = $("#hero-select");
    const menu = $("#hero-select-menu");
    const trigger = $("#hero-select-trigger");
    if (!root || !menu || !trigger) return;
    renderHeroPicker();
    menu.hidden = !open;
    root.classList.toggle("is-open", open);
    const containingCard = root.closest(".card");
    if (containingCard) containingCard.classList.toggle("hero-menu-open", open);
    trigger.setAttribute("aria-expanded", open ? "true" : "false");
    if (open) {
      positionHeroMenu();
      highlightHero(heroHighlighted, false);
      if (focusMenu) menu.focus();
    } else {
      root.classList.remove("open-up");
      heroTypeahead = "";
    }
  }

  function moveHeroHighlight(delta) {
    highlightHero(heroHighlighted + delta, true);
  }

  function typeaheadHero(key) {
    heroTypeahead += key.toLowerCase();
    window.clearTimeout(heroTypeaheadTimer);
    heroTypeaheadTimer = window.setTimeout(() => { heroTypeahead = ""; }, 700);
    const options = heroOptions();
    const match = options.findIndex((option) => option.textContent.toLowerCase().startsWith(heroTypeahead));
    if (match >= 0) highlightHero(match, true);
  }

  function chooseHero(hero) {
    heroKeys.forEach((key) => { state.settings[key] = key === hero; });
    state.settings.Nohero = !hero;
    if (!hero) state.settings.YourHero = false;
    syncHeroControls();
    syncLegitChanceControl();
    markProfileDirty();
    sendSettings();
    setHeroOpen(false);
    $("#hero-select-trigger")?.focus();
  }

  function selectedHero() {
    return heroKeys.find((hero) => state.settings[hero] === true) || "";
  }

  function syncHeroControls() {
    const value = $("#hero-select-value");
    const toggle = $("#hero-response-enabled");
    const hero = selectedHero();
    if (value) value.textContent = heroLabels[hero] || hero || "None";
    if (toggle) {
      toggle.checked = hero !== "" && state.settings.YourHero === true && state.settings.Nohero !== true;
      toggle.disabled = hero === "";
      toggle.closest(".toggle-row")?.classList.toggle("disabled", hero === "");
    }
    const options = heroOptions();
    const selectedIndex = options.findIndex((option) => option.dataset.hero === hero);
    if (selectedIndex >= 0) {
      heroHighlighted = selectedIndex;
      highlightHero(heroHighlighted, false);
    }
    const note = $("#hero-response-note");
    if (note) {
      note.textContent = hero === "Peacekeeper"
        ? "Optional: side deflect timing is forced to 100 ms when timings are applied."
        : hero === ""
          ? "Choose a hero first."
          : "Generic Parry, Crushing, and Deflect can still take priority.";
    }
    syncTimingContext();
  }

  function syncLegitChanceControl() {
    const chance = $('[data-setting="LegitParryChance"]');
    if (chance) chance.disabled = state.settings.Legit !== true;
    const fallback = $('[data-setting="BulwarkFallback"]');
    if (fallback) {
      fallback.disabled = !(state.settings.Autoblock === true && state.settings.Legit === true && state.settings.Parry === true &&
        state.settings.YourHero === true && state.settings.Nohero !== true && state.settings.Blackprior === true);
    }
    const crushingFallbackChance = $('[data-setting="CrushingFallbackChance"]');
    if (crushingFallbackChance) {
      crushingFallbackChance.disabled = !(state.settings.Autoblock === true && state.settings.Legit === true && state.settings.Parry === true &&
        state.settings.Crushing === true && state.settings.BulwarkFallback === true && state.settings.YourHero === true &&
        state.settings.Nohero !== true && state.settings.Blackprior === true);
    }
    const deflectFallbackChance = $('[data-setting="DeflectFallbackChance"]');
    if (deflectFallbackChance) {
      deflectFallbackChance.disabled = !(state.settings.Autoblock === true && state.settings.Legit === true && state.settings.Parry === true &&
        state.settings.Deflect === true);
    }
    const orangeLight = $('[data-setting="OrangeLight"]');
    if (orangeLight) orangeLight.disabled = state.settings.Unblockables !== true;
  }

  function updateStatus(status) {
    const incomingStatus = status || {};
    if (profileAwaitingClean && incomingStatus.profileDirty === false) {
      profileDraftDirty = false;
      profileAwaitingClean = false;
    }
    state.status = Object.assign({}, incomingStatus, {
      profileDirty: profileDraftDirty || incomingStatus.profileDirty === true
    });
    const running = Boolean(state.status.running);
    const paused = state.status.paused === true;
    const error = state.status.error || "";
    const marker = String(state.status.marker || "MISSING").toUpperCase();
    const hold = String(state.status.hold || "UP").toUpperCase();
    const indicator = String(state.status.indicator || "NO").toUpperCase();
    const guard = state.status.guard || "-";
    const source = String(state.status.source || "OFF").toUpperCase();
    const sourceSlot = Number(state.status.sourceSlot);
    const virtual = String(state.status.virtualState || "OFF").toUpperCase();
    const mode = state.status.mode || "ViGEm";
    const version = String(state.status.version || "2.0.0");
    const build = String(state.status.build || "");
    const loop = Number(state.status.loop || 0);
    const orangeParry = state.status.orangeParry === true;
    const visionOverlay = state.status.visionOverlay === true;
    const anchorScan = state.status.anchorScan !== false;
    const telemetry = state.status.telemetry || {};
    const telemetryRecording = telemetry.recording === true;
    const autoDodgeBindButton = $("#auto-dodge-bind-button");
    const profileName = String(state.status.profile || "Default");
    const profileDirty = state.status.profileDirty === true;
    const timingsDirty = timingDraftDirty || state.status.timingsDirty === true;
    const profiles = Array.isArray(state.status.profiles) && state.status.profiles.length > 0
      ? state.status.profiles
      : ["Default"];
    renderProfileSelect(profiles, profileName);
    renderProfileState(profileName, profileDirty);
    renderApplyState(timingsDirty);
    renderBehavior(state.status.behavior, profileName);
    syncTimingContext();
    renderReadiness(state.status, profileName);
    renderLastReaction(state.status.lastReaction);

    const top = $("#top-runtime");
    top.innerHTML = '<span class="status-dot ' + (running ? "green" : "") + '"></span> ' + (running ? "RUNNING" : "STANDBY");
    $("#heading-badge").textContent = error ? "ERROR" : (running ? (paused ? "PAUSED" : "LIVE") : "READY TO CONFIGURE");
    $("#runtime-title").textContent = error ? "Runtime error" : (running ? (paused ? "Bot is paused" : "Bot is active") : "Waiting for launch");
    $("#runtime-copy").textContent = error || (running
      ? (paused ? "Reaction delivery is paused. Resume when your source controller is ready." : "The reaction loop is live. Return to the game when your source controller is ready.")
      : "Configure a feature set, then start the bot before returning to the game.");
    $("#start-button").disabled = running;
    $("#start-button").textContent = running ? (paused ? "Bot paused" : "Bot running") : "Start bot";
    const pauseButton = $("#pause-button");
    if (pauseButton) {
      pauseButton.disabled = !running;
      pauseButton.textContent = paused ? "Resume bot" : "Pause bot";
    }
    $("#runtime-orb").classList.toggle("running", running);

    setMetric("metric-marker", marker, marker === "FOUND");
    setMetric("metric-hold", hold, hold === "DOWN");
    setMetric("metric-indicator", indicator, indicator === "YES");
    setMetric("metric-guard", guard, guard !== "-" && guard !== "UNKNOWN");
    setMetric("metric-mode", mode, mode.toLowerCase() === "vigem");
    setMetric("metric-source", source === "ON" && sourceSlot >= 0 ? "ON / " + sourceSlot : source, source === "ON");
    setMetric("metric-virtual", virtual, virtual === "ON");
    setMetric("metric-loop", loop + " Hz", loop > 0);
    $("#sidebar-input").textContent = mode + " / " + (source === "ON" ? "source " + sourceSlot : "idle");
    const versionLabel = $("#sidebar-version");
    if (versionLabel) versionLabel.textContent = "DANBOT / v" + version + (build ? " · " + build : "");
    const rtSent = state.status.rtSent ?? state.status.parryAttempts ?? state.status.parryCount ?? 0;
    $("#parry-count").textContent = String(rtSent) + " RT sent";
    const confirmed = state.status.parriesConfirmed || 0;
    const confirmedCount = $("#parry-confirmed-count");
    if (confirmedCount) confirmedCount.textContent = String(confirmed) + " Parries confirmed";
    const orangeButton = $("#orange-parry-button");
    if (orangeButton) {
      orangeButton.textContent = "Orange parry: " + (orangeParry ? "ON" : "OFF") + " · F5";
      orangeButton.classList.toggle("active", orangeParry);
    }
    const visionButton = $("#vision-overlay-button");
    if (visionButton) {
      visionButton.textContent = "Vision overlay: " + (visionOverlay ? "ON" : "OFF") + " - F7";
      visionButton.classList.toggle("active", visionOverlay);
    }
    const anchorScanButton = $("#anchor-scan-button");
    if (anchorScanButton) {
      anchorScanButton.textContent = "Anchor scan: " + (anchorScan ? "ON" : "OFF");
      anchorScanButton.classList.toggle("active", anchorScan);
    }
    const telemetryButton = $("#telemetry-button");
    if (telemetryButton) {
      telemetryButton.textContent = "Telemetry: " + (telemetryRecording ? "ON" : "OFF");
      telemetryButton.classList.toggle("active", telemetryRecording);
    }
    const telemetryStatus = $("#telemetry-status");
    if (telemetryStatus) {
      const seconds = Number(telemetry.durationSeconds || 0);
      telemetryStatus.textContent = telemetryRecording
        ? "Telemetry " + (telemetry.label || "Other") + " · " + seconds + "s · " + Number(telemetry.failures || 0) + " failures · " + Number(telemetry.dropped || 0) + " dropped"
        : "Telemetry OFF";
    }
    if (autoDodgeBindButton) {
      autoDodgeBindButton.textContent = state.status.bindingAutoDodge
        ? "Press controller button..."
        : "Dodge bind: " + String(state.status.autoDodgeBind || "UNBOUND");
      autoDodgeBindButton.classList.toggle("active", state.status.bindingAutoDodge === true);
    }
  }

  function setMetric(id, text, good) {
    const element = $("#" + id);
    if (!element) return;
    element.textContent = text;
    element.classList.toggle("good", Boolean(good));
    element.classList.toggle("alert", text === "MISSING" || text === "OFF" || text === "UP");
  }

  function profileOptions() {
    return $$("#profile-select-menu [role=option]");
  }

  function renderProfileState(profileName, dirty) {
    const profileState = $("#profile-state");
    if (!profileState) return;
    profileState.replaceChildren();
    const dot = document.createElement("span");
    dot.className = "state-dot";
    profileState.append(dot, document.createTextNode(dirty ? "Profile not saved" : "Profile saved"));
    profileState.title = profileName;
    profileState.classList.toggle("dirty", dirty);
    $("#save-profile-button").disabled = !dirty;
  }

  function renderReadiness(status, profileName) {
    const virtualReady = String(status.virtualState || "OFF").toUpperCase() === "ON";
    const width = Number(state.settings.res1);
    const height = Number(state.settings.res2);
    const resolutionReady = Number.isInteger(width) && Number.isInteger(height) && width > 0 && height > 0 && width >= height;
    setReadiness("readiness-input", virtualReady, "Virtual controller", virtualReady ? "Ready to start" : "Unavailable — reconnect ViGEm");
    setReadiness("readiness-resolution", resolutionReady, "Screen calibration", resolutionReady ? width + " × " + height : "Set a valid resolution in Timing");
    setReadiness("readiness-profile", true, "Active profile", profileName + (status.profileDirty === true ? " · unsaved edits" : " · saved"));
  }

  function setReadiness(id, good, title, detail) {
    const root = $("#" + id);
    if (!root) return;
    const dot = root.querySelector(".state-dot");
    if (dot) dot.classList.toggle("green", Boolean(good));
    const labels = root.querySelectorAll("b, small");
    if (labels[0]) labels[0].textContent = title;
    if (labels[1]) labels[1].textContent = detail;
    root.classList.toggle("ready", Boolean(good));
    root.classList.toggle("not-ready", !good);
  }

  function renderLastReaction(lastReaction) {
    const reaction = lastReaction || {};
    const stateLabel = String(reaction.state || "NONE");
    const stateElement = $("#last-reaction-state");
    const confirmationElement = $("#last-reaction-confirmation");
    const directionElement = $("#last-reaction-direction");
    const delayElement = $("#last-reaction-delay");
    const reasonElement = $("#last-reaction-reason");
    const hasReaction = stateLabel !== "NONE";
    if (stateElement) stateElement.textContent = hasReaction ? stateLabel : "No reaction yet";
    if (confirmationElement) {
      confirmationElement.textContent = String(reaction.confirmation || (hasReaction ? "OBSERVED" : "WAITING"));
      confirmationElement.className = "badge badge-muted " + (String(reaction.confirmation || "").toLowerCase().replaceAll(" ", "-") || "waiting");
    }
    if (directionElement) directionElement.textContent = String(reaction.direction || "-") || "-";
    if (delayElement) delayElement.textContent = Number(reaction.delayMs) >= 0 ? Number(reaction.delayMs) + " ms" : "Not applicable";
    if (reasonElement) reasonElement.textContent = String(reaction.reason || "Start the bot to see why the next reaction was sent or skipped.");
  }

  function syncTimingContext() {
    const hero = selectedHero();
    const behavior = state.status.behavior || {};
    const timingHint = (key, fallback) => behaviorValue(behavior, key, "") || fallback;
    const override = $("#peacekeeper-override");
    if (override) override.hidden = hero !== "Peacekeeper";

    const deflectAffects = $("#deflect-affects");
    if (deflectAffects) deflectAffects.textContent = timingHint(
      "deflectTiming",
      "Timing eligibility is reported after the current settings are acknowledged."
    );

    const parryAffects = $("#parry-affects");
    if (parryAffects) parryAffects.textContent = timingHint(
      "parryTiming",
      "Timing eligibility is reported after the current settings are acknowledged."
    );

    const dodgeAffects = $("#dodge-affects");
    if (dodgeAffects) dodgeAffects.textContent = timingHint(
      "dodgeTiming",
      "Timing eligibility is reported after the current settings are acknowledged."
    );
    const context = $("#timing-context");
    if (context) context.textContent = hero === "Peacekeeper"
      ? "Peacekeeper is selected: side deflect delays are forced to 100 ms when applied; the Top value remains editable."
      : "Timing values are edited locally and remain pending until Apply timings succeeds.";
  }

  function validationMessage(control) {
    const value = control.value.trim();
    const min = Number(control.min);
    const max = Number(control.max);
    if (!value) return "Enter a value.";
    if (!/^[-+]?\d+$/.test(value)) return "Use a whole number.";
    const number = Number(value);
    if (!Number.isFinite(number)) return "Use a valid number.";
    if (Number.isFinite(min) && number < min) return "Must be at least " + min + ".";
    if (Number.isFinite(max) && number > max) return "Must be at most " + max + ".";
    return "";
  }

  function validateTimingControl(control) {
    if (!control || (!control.dataset.timing && !control.dataset.calibration)) return true;
    const key = control.dataset.setting;
    let message = validationMessage(control);
    const error = document.querySelector('[data-error-for="' + key + '"]');
    if (error) error.textContent = message;
    const field = control.closest(".validation-field");
    if (field) field.classList.toggle("invalid", Boolean(message));
    control.setAttribute("aria-invalid", message ? "true" : "false");
    control.setCustomValidity(message);
    return !message;
  }

  function validateTimingInputs(showSummary) {
    let valid = true;
    $$('[data-setting][data-timing], [data-setting][data-calibration]').forEach((control) => {
      valid = validateTimingControl(control) && valid;
    });
    const width = $("[data-setting=\"res1\"]");
    const height = $("[data-setting=\"res2\"]");
    if (width && height && !validationMessage(width) && !validationMessage(height) && Number(width.value) < Number(height.value)) {
      const message = "Width must be at least height.";
      const error = document.querySelector('[data-error-for="res1"]');
      if (error) error.textContent = message;
      width.closest(".validation-field")?.classList.add("invalid");
      width.setAttribute("aria-invalid", "true");
      width.setCustomValidity(message);
      valid = false;
    }
    const summary = $("#timing-validation-summary");
    if (summary) {
      summary.hidden = valid || !showSummary;
      summary.textContent = valid ? "" : "Fix the highlighted timing or calibration values before applying.";
    }
    return valid;
  }

  function renderApplyState(dirty) {
    const timingState = $("#timing-state");
    const applyButton = $("#apply-button");
    if (!timingState || !applyButton) return;
    timingState.lastChild.textContent = applyPending ? "Applying timings…" : (dirty ? "Timings not applied" : "Timings applied");
    timingState.classList.toggle("dirty", dirty && !applyPending);
    timingState.classList.toggle("pending", applyPending);
    applyButton.disabled = applyPending || !dirty;
    applyButton.textContent = applyPending ? "Applying…" : "Apply timings";
  }

  function behaviorValue(behavior, key, fallback) {
    if (!behavior) return fallback;
    return behavior[key] ?? behavior[key.charAt(0).toUpperCase() + key.slice(1)] ?? fallback;
  }

  function renderBehavior(behavior, profileName) {
    if (!behavior) return;
    const hero = behaviorValue(behavior, "hero", "None");
    $("#behavior-hero").textContent = hero === "Blackprior" ? "Black Prior" : hero === "Jiangjun" ? "Jiang Jun" : hero;
    $("#behavior-profile").textContent = profileName;
    $("#behavior-f-action").textContent = behaviorValue(behavior, "fAction", "None");
    $("#behavior-f-detail").textContent = behaviorValue(behavior, "fDetail", "No reaction is configured.");
    $("#behavior-e-action").textContent = behaviorValue(behavior, "eAction", "None");
    $("#behavior-e-detail").textContent = behaviorValue(behavior, "eDetail", "No reaction is configured.");

    const notices = behaviorValue(behavior, "notices", []);
    const noticeRoot = $("#behavior-notices");
    noticeRoot.replaceChildren();
    (Array.isArray(notices) ? notices : []).forEach((notice) => {
      const item = document.createElement("p");
      item.textContent = notice;
      noticeRoot.appendChild(item);
    });

    $("#hero-detail-title").textContent = hero === "None" ? "No hero selected" : $("#behavior-hero").textContent;
    $("#hero-detail-behavior").textContent = behaviorValue(behavior, "heroBehavior", "No hero-specific behavior is selected.");
    $("#hero-detail-status").textContent = behaviorValue(behavior, "heroStatus", "Inactive.");
    $("#hero-detail-requirements").textContent = behaviorValue(behavior, "heroRequirements", "Select a hero.");
    $("#hero-detail-timings").textContent = behaviorValue(behavior, "heroTimings", "No hero timing applies.");
    const enabled = behaviorValue(behavior, "heroResponseEnabled", false) === true;
    const heroMode = behaviorValue(behavior, "heroMode", enabled ? "ENABLED" : "INACTIVE");
    const stateLabel = $("#hero-detail-state");
    stateLabel.textContent = heroMode;
    stateLabel.classList.toggle("active", ["ENABLED", "OVERRIDE ACTIVE", "MODIFIER ACTIVE"].includes(heroMode));
    stateLabel.classList.toggle("info", heroMode === "TIMING ONLY");
  }

  function markProfileDirty() {
    applyRevision.markEdit();
    profileDraftDirty = true;
    profileAwaitingClean = false;
    state.status = Object.assign({}, state.status, { profileDirty: true });
    renderProfileState(String(state.status.profile || "Default"), true);
  }

  function markTimingsDirty() {
    applyRevision.markTimingEdit();
    timingDraftDirty = true;
    renderApplyState(true);
  }

  function postProfileMutation(type, extra) {
    profileAwaitingClean = true;
    post(type, extra);
  }

  function sendSettingsThenProfileMutation(type, extra) {
    sendSettings();
    postProfileMutation(type, extra);
  }

  function renderProfileSelect(profiles, activeName) {
    const root = $("#profile-select");
    const menu = $("#profile-select-menu");
    const value = $("#profile-select-value");
    const trigger = $("#profile-select-trigger");
    if (!root || !menu || !value || !trigger) return;

    const names = [];
    const seen = new Set();
    profiles.forEach((profile) => {
      const name = String(profile);
      const key = name.toLowerCase();
      if (seen.has(key)) return;
      seen.add(key);
      names.push(name);
    });
    const activeKey = String(activeName).toLowerCase();
    const listChanged = names.length !== profileRenderedNames.length || names.some((name, index) => name !== profileRenderedNames[index]);
    const activeChanged = activeKey !== profileRenderedActive.toLowerCase();

    if (listChanged) {
      menu.replaceChildren();
      names.forEach((name, index) => {
        const option = document.createElement("button");
        option.type = "button";
        option.className = "profile-select-option";
        option.id = "profile-option-" + index;
        option.setAttribute("role", "option");
        option.dataset.profile = name;
        option.textContent = name;
        option.addEventListener("pointerenter", () => {
          const optionIndex = profileOptions().indexOf(option);
          if (optionIndex >= 0) highlightProfile(optionIndex, false);
        });
        option.addEventListener("click", (event) => {
          event.preventDefault();
          event.stopPropagation();
          requestProfileSelection(name);
        });
        menu.appendChild(option);
      });
      profileRenderedNames = names;
      profileHighlighted = 0;
    }

    const activeIndex = names.findIndex((name) => name.toLowerCase() === activeKey);
    const safeIndex = activeIndex >= 0 ? activeIndex : 0;
    if (listChanged || activeChanged) profileHighlighted = safeIndex;
    profileRenderedActive = activeName;
    value.textContent = names[safeIndex] || "Default";
    profileOptions().forEach((option) => {
      option.setAttribute("aria-selected", option.dataset.profile.toLowerCase() === activeKey ? "true" : "false");
    });
    highlightProfile(profileHighlighted, false);
  }

  function highlightProfile(index, scroll) {
    const options = profileOptions();
    if (!options.length) {
      profileHighlighted = -1;
      return;
    }
    profileHighlighted = Math.max(0, Math.min(index, options.length - 1));
    options.forEach((option, optionIndex) => {
      option.classList.toggle("highlighted", optionIndex === profileHighlighted);
    });
    const menu = $("#profile-select-menu");
    if (menu) {
      const active = options[profileHighlighted];
      menu.setAttribute("aria-activedescendant", active.id || "");
      if (scroll && active) active.scrollIntoView({ block: "nearest" });
    }
  }

  function setProfileOpen(open, focusMenu) {
    const root = $("#profile-select");
    const menu = $("#profile-select-menu");
    const trigger = $("#profile-select-trigger");
    if (!root || !menu || !trigger) return;
    menu.hidden = !open;
    root.classList.toggle("is-open", open);
    const containingCard = root.closest(".card");
    if (containingCard) containingCard.classList.toggle("profile-menu-open", open);
    trigger.setAttribute("aria-expanded", open ? "true" : "false");
    if (open) {
      highlightProfile(profileHighlighted, false);
      if (focusMenu) menu.focus();
    } else {
      profileTypeahead = "";
    }
  }

  function moveProfileHighlight(delta) {
    highlightProfile(profileHighlighted + delta, true);
  }

  function typeaheadProfile(key) {
    profileTypeahead += key.toLowerCase();
    window.clearTimeout(profileTypeaheadTimer);
    profileTypeaheadTimer = window.setTimeout(() => { profileTypeahead = ""; }, 700);
    const options = profileOptions();
    const match = options.findIndex((option) => option.textContent.toLowerCase().startsWith(profileTypeahead));
    if (match >= 0) highlightProfile(match, true);
  }

  function requestProfileSelection(name) {
    if (applyPending) return showToast("Wait for Apply to finish before switching profiles.", "info");
    const current = String(state.status.profile || "Default");
    setProfileOpen(false);
    if (name.toLowerCase() === current.toLowerCase()) return;
    if (state.status.profileDirty === true) {
      showConfirmDialog(
        "Discard profile changes?",
        "Unsaved changes in " + current + " will be discarded before loading " + name + ".",
        "Discard",
        () => {
          profileLoadPending = true;
          postProfileMutation("profile-select", { name, discard: true, draftDirty: true });
        }
      );
      return;
    }
    profileLoadPending = true;
    postProfileMutation("profile-select", { name, discard: false, draftDirty: false });
  }

  function showDialog(title, body, eyebrow) {
    modalCallback = null;
    modalReturnFocus = document.activeElement;
    $("#modal-title").textContent = title || "Message";
    $("#modal-eyebrow").textContent = eyebrow || "MESSAGE";
    $("#modal-body").textContent = body || "";
    $("#modal-form").hidden = true;
    $("#modal-actions").hidden = true;
    $("#modal-root").hidden = false;
    $("#modal-close-button")?.focus();
  }

  function closeDialog() {
    $("#modal-root").hidden = true;
    $("#modal-form").hidden = true;
    $("#modal-actions").hidden = true;
    modalCallback = null;
    const returnFocus = modalReturnFocus;
    modalReturnFocus = null;
    if (returnFocus && returnFocus.isConnected && typeof returnFocus.focus === "function") returnFocus.focus();
  }

  function showConfirmDialog(title, body, confirmLabel, callback) {
    modalReturnFocus = document.activeElement;
    modalCallback = callback;
    $("#modal-eyebrow").textContent = "CONFIRM ACTION";
    $("#modal-title").textContent = title || "Confirm";
    $("#modal-body").textContent = body || "";
    $("#modal-form").hidden = true;
    $("#modal-actions").hidden = false;
    $("#modal-confirm-button").textContent = confirmLabel || "Confirm";
    $("#modal-root").hidden = false;
    $("#modal-cancel-button").focus();
  }

  function showInputDialog(title, body, label, initialValue, confirmLabel, callback) {
    modalReturnFocus = document.activeElement;
    modalCallback = callback;
    $("#modal-eyebrow").textContent = "PROFILE";
    $("#modal-title").textContent = title || "Enter a value";
    $("#modal-body").textContent = body || "";
    $("#modal-input-label").textContent = label || "Value";
    $("#modal-input").value = initialValue || "";
    $("#modal-form").hidden = false;
    $("#modal-actions").hidden = false;
    $("#modal-confirm-button").textContent = confirmLabel || "Confirm";
    $("#modal-root").hidden = false;
    const input = $("#modal-input");
    input.focus();
    input.select();
  }

  function confirmModal() {
    const callback = modalCallback;
    const hasInput = !$("#modal-form").hidden;
    const value = hasInput ? $("#modal-input").value.trim() : null;
    closeDialog();
    if (callback) callback(value);
  }

  function showToast(message, kind) {
    const toast = document.createElement("div");
    toast.className = "toast " + (kind || "");
    toast.textContent = message;
    $("#toast-stack").appendChild(toast);
    window.setTimeout(() => toast.remove(), kind === "error" ? 4200 : 2600);
  }

  function selectView(name) {
    $$('[data-view-target]').forEach((button) => button.classList.toggle("active", button.dataset.viewTarget === name));
    $$('[data-view]').forEach((view) => view.classList.toggle("active", view.dataset.view === name));
  }

  function toggleBehaviorDetails() {
    const dock = $(".behavior-dock");
    const button = $("#behavior-expand");
    if (!dock || !button) return;
    const expanded = !dock.classList.contains("is-expanded");
    dock.classList.toggle("is-expanded", expanded);
    button.setAttribute("aria-expanded", expanded ? "true" : "false");
    button.textContent = expanded ? "Hide details" : "Show details";
  }

  function handleAction(action) {
    if (action === "close-modal") return closeDialog();
    if (action === "toggle-behavior-details") return toggleBehaviorDetails();
    if (action === "start") {
      if (!validateTimingInputs(true)) return showToast("Fix the highlighted timing values before starting the bot.", "error");
      return beginAcknowledgedApply("start");
    }
    if (action === "toggle-pause") return post("toggle-pause");
    if (action === "scan") return sendSettingsThen("scan");
    if (action === "test") return post("test");
    if (action === "resolution") {
      if (!validateTimingInputs(true)) return showToast("Fix the highlighted values before applying calibration.", "error");
      return sendSettingsThen("resolution");
    }
    if (action === "save") return sendSettingsThen("save");
    if (action === "load") return requestProfileLoad();
    if (action === "profile-load") return requestProfileLoad();
    if (action === "profile-save") return sendSettingsThenProfileMutation("profile-save");
    if (action === "profile-save-as") {
      showInputDialog(
        "Save profile as",
        "Create a named profile. Use letters, numbers, spaces, dashes, or underscores.",
        "Profile name",
        state.status.profile || "",
        "Save",
        (name) => {
          if (!name) return showToast("Enter a profile name.", "error");
          const exists = Array.isArray(state.status.profiles) && state.status.profiles.some((profile) => profile.toLowerCase() === name.toLowerCase());
          if (exists) {
            showConfirmDialog(
              "Overwrite profile?",
              "A profile named " + name + " already exists. Replace its saved settings?",
              "Overwrite",
              () => sendSettingsThenProfileMutation("profile-save-as", { name })
            );
          } else {
            sendSettingsThenProfileMutation("profile-save-as", { name });
          }
        }
      );
      return;
    }
    if (action === "profile-delete") {
      const name = state.status.profile || "Default";
      if (name.toLowerCase() === "default") return showToast("The Default profile cannot be deleted.", "error");
      const draftDirty = state.status.profileDirty === true;
      showConfirmDialog(
        "Delete profile?",
        "Delete " + name + " and its saved settings?" + (draftDirty ? " Unsaved changes will also be discarded." : "") + " This cannot be undone.",
        "Delete",
        () => {
          profileLoadPending = true;
          postProfileMutation("profile-delete", { name, discard: draftDirty, draftDirty });
        }
      );
      return;
    }
    if (action === "apply") {
      if (!validateTimingInputs(true)) return showToast("Fix the highlighted values before applying timings.", "error");
      return beginAcknowledgedApply("apply");
    }
    if (action === "howto") return post("howto");
    if (action === "readme") return post("readme");
    if (action === "reload") return post("reload");
    if (action === "minimize") return post("minimize");
    if (action === "close") return post("close");
    if (action === "drag") return post("drag");
    if (action === "toggle-orange-parry") return post("orange-parry");
    if (action === "vision-overlay") return post("vision-overlay");
    if (action === "anchor-scan") return post("anchor-scan");
    if (action === "bind-auto-dodge") return post("bind-auto-dodge");
    if (action === "telemetry") return post("telemetry", { label: $("#telemetry-label").value });
    if (action === "export-telemetry") return post("export-telemetry");
  }

  $$('[data-view-target]').forEach((button) => button.addEventListener("click", () => selectView(button.dataset.viewTarget)));
  $$('[data-action]').forEach((button) => button.addEventListener("click", (event) => {
    event.stopPropagation();
    handleAction(button.dataset.action);
  }));
  $$('[data-setting]').forEach((control) => {
    const eventName = control.type === "checkbox" || control.tagName === "SELECT" ? "change" : "input";
    control.addEventListener(eventName, () => {
      if (hydrating) return;
      if (control.type === "checkbox" && control.checked && control.closest(".hero-list")) {
        $$(".hero-list input[data-setting]").forEach((other) => {
          if (other === control) return;
          other.checked = false;
          state.settings[other.dataset.setting] = false;
        });
      }
      if (control.type === "checkbox" && control.checked && (control.dataset.setting === "Leftdodge" || control.dataset.setting === "Rightdodge")) {
        const otherName = control.dataset.setting === "Leftdodge" ? "Rightdodge" : "Leftdodge";
        const other = $(`[data-setting="${otherName}"]`);
        if (other) other.checked = false;
        state.settings[otherName] = false;
      }
      state.settings[control.dataset.setting] = control.type === "checkbox" ? control.checked : control.value;
      if (control.dataset.setting === "YourHero") state.settings.Nohero = !control.checked;
      if (["Autoblock", "Legit", "Parry", "Crushing", "Deflect", "BulwarkFallback", "YourHero", "Nohero", "Blackprior", "Nuxia", "Unblockables"].includes(control.dataset.setting)) syncLegitChanceControl();
      if (control.dataset.timing || control.dataset.calibration) validateTimingControl(control);
      syncTimingContext();
      markProfileDirty();
      if (control.type === "checkbox") sendSettings();
      else markTimingsDirty();
    });
  });
  const heroSelect = $("#hero-select");
  const heroTrigger = $("#hero-select-trigger");
  const heroMenu = $("#hero-select-menu");
  if (heroSelect && heroTrigger && heroMenu) {
    renderHeroPicker();
    heroTrigger.addEventListener("click", () => setHeroOpen(heroMenu.hidden));
    heroTrigger.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        setHeroOpen(true, true);
        moveHeroHighlight(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        setHeroOpen(heroMenu.hidden, heroMenu.hidden);
      } else if (event.key === "Escape" && !heroMenu.hidden) {
        event.preventDefault();
        setHeroOpen(false);
      }
    });
    heroMenu.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        moveHeroHighlight(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Home" || event.key === "End") {
        event.preventDefault();
        highlightHero(event.key === "Home" ? 0 : heroOptions().length - 1, true);
      } else if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        const option = heroOptions()[heroHighlighted];
        if (option) chooseHero(option.dataset.hero);
      } else if (event.key === "Escape") {
        event.preventDefault();
        setHeroOpen(false);
        heroTrigger.focus();
      } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
        typeaheadHero(event.key);
      }
    });
    document.addEventListener("pointerdown", (event) => {
      if (!heroSelect.contains(event.target)) setHeroOpen(false);
    });
    window.addEventListener("resize", () => {
      if (!heroMenu.hidden) positionHeroMenu();
    });
    $(".main-wrap")?.addEventListener("scroll", () => {
      if (!heroMenu.hidden) positionHeroMenu();
    }, { passive: true });
  }
  const profileSelect = $("#profile-select");
  const profileTrigger = $("#profile-select-trigger");
  const profileMenu = $("#profile-select-menu");
  if (profileSelect && profileTrigger && profileMenu) {
    profileTrigger.addEventListener("click", () => setProfileOpen(profileMenu.hidden));
    profileTrigger.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        setProfileOpen(true, true);
        moveProfileHighlight(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        const opening = profileMenu.hidden;
        setProfileOpen(opening, opening);
      } else if (event.key === "Escape" && !profileMenu.hidden) {
        event.preventDefault();
        setProfileOpen(false);
      }
    });
    profileMenu.addEventListener("keydown", (event) => {
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        moveProfileHighlight(event.key === "ArrowDown" ? 1 : -1);
      } else if (event.key === "Home" || event.key === "End") {
        event.preventDefault();
        highlightProfile(event.key === "Home" ? 0 : profileOptions().length - 1, true);
      } else if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        const option = profileOptions()[profileHighlighted];
        if (option) requestProfileSelection(option.dataset.profile);
      } else if (event.key === "Escape") {
        event.preventDefault();
        setProfileOpen(false);
        profileTrigger.focus();
      } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
        typeaheadProfile(event.key);
      }
    });
    document.addEventListener("pointerdown", (event) => {
      if (!profileSelect.contains(event.target)) setProfileOpen(false);
    });
  }

  function requestProfileLoad() {
    if (applyPending) return showToast("Wait for Apply to finish before loading a profile.", "info");
    const profileName = String(state.status.profile || "Default");
    const draftDirty = state.status.profileDirty === true;
    if (!draftDirty) {
      profileLoadPending = true;
      return postProfileMutation("profile-load", { discard: false, draftDirty: false });
    }
    showConfirmDialog(
      "Discard profile changes?",
      "Unsaved changes in " + profileName + " will be discarded and the saved profile will be loaded.",
      "Discard",
      () => {
        profileLoadPending = true;
        postProfileMutation("profile-load", { discard: true, draftDirty: true });
      }
    );
  }
  $(".titlebar").addEventListener("pointerdown", (event) => {
    if (!event.target.closest("button")) post("drag");
  });
  $("#modal-confirm-button").addEventListener("click", confirmModal);
  $("#modal-cancel-button").addEventListener("click", closeDialog);
  $("#modal-root").addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      event.preventDefault();
      closeDialog();
      return;
    }
    if (event.key !== "Tab") return;
    const focusable = $$("#modal-root button:not([disabled]), #modal-root input:not([disabled])").filter((element) => !element.closest("[hidden]"));
    if (!focusable.length) return;
    const current = focusable.indexOf(document.activeElement);
    const next = (current + (event.shiftKey ? -1 : 1) + focusable.length) % focusable.length;
    event.preventDefault();
    focusable[next].focus();
  });

  if (bridge) {
    bridge.addEventListener("message", (event) => {
      const message = event.data || {};
      if (message.type === "init") {
        applySettings(message.settings);
        updateStatus(message.status);
      } else if (message.type === "status") {
        updateStatus(message.status || message);
      } else if (message.type === "settings") {
        if (applyPending) {
          applyRevision.acknowledge(message.settings);
          return;
        }
        if (profileLoadPending) {
          timingDraftDirty = false;
          profileLoadPending = false;
        }
        applySettings(message.settings);
      } else if (message.type === "apply-result") {
        if (applyPending && (!applyRequestId || message.requestId === applyRequestId)) {
          const revision = applyRevision.complete(message.requestId, message.success === true);
          if (revision.ignored) return;
          applyPending = false;
          applyRequestId = "";
          if (revision.shouldHydrate) {
            applySettings(revision.acknowledgedSettings);
          }
          if (revision.shouldClearTimingDirty) timingDraftDirty = false;
          if (revision.success && revision.hasNewerTimingEdits) {
            showToast("Earlier timings applied; newer edits are still pending.", "warning");
          }
          renderApplyState(timingDraftDirty || state.status.timingsDirty === true);
        }
      } else if (message.type === "toast") {
        showToast(message.message, message.kind);
      } else if (message.type === "dialog") {
        showDialog(message.title, message.body, message.eyebrow);
      }
    });
    post("ready");
  } else {
    showToast("WebView2 bridge unavailable", "error");
  }
})();
