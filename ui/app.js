(function () {
  "use strict";

  const bridge = window.chrome && window.chrome.webview;
  const state = { settings: {}, status: {} };
  let hydrating = false;
  let profileHighlighted = -1;
  let profileRenderedNames = [];
  let profileRenderedActive = "";
  let profileDraftDirty = false;
  let profileAwaitingClean = false;
  let profileTypeahead = "";
  let profileTypeaheadTimer = 0;
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
      if (control.type === "checkbox") control.checked = Boolean(value);
      else control.value = value;
    });
    hydrating = false;
    syncLegitChanceControl();
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
    const profiles = Array.isArray(state.status.profiles) && state.status.profiles.length > 0
      ? state.status.profiles
      : ["Default"];
    renderProfileSelect(profiles, profileName);
    renderProfileState(profileName, profileDirty);

    const top = $("#top-runtime");
    top.innerHTML = '<span class="status-dot ' + (running ? "green" : "") + '"></span> ' + (running ? "RUNNING" : "STANDBY");
    $("#heading-badge").textContent = error ? "ERROR" : (running ? "LIVE" : "READY TO CONFIGURE");
    $("#runtime-title").textContent = error ? "Runtime error" : (running ? "Bot is active" : "Waiting for launch");
    $("#runtime-copy").textContent = error || (running ? "The reaction loop is live. Return to the game when your source controller is ready." : "Configure a feature set, then start the bot before returning to the game.");
    $("#start-button").disabled = running;
    $("#start-button").textContent = running ? "Bot running" : "Start bot";
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
    profileState.textContent = profileName + (dirty ? " · unsaved" : " · saved");
    profileState.classList.toggle("dirty", dirty);
  }

  function markProfileDirty() {
    profileDraftDirty = true;
    profileAwaitingClean = false;
    state.status = Object.assign({}, state.status, { profileDirty: true });
    renderProfileState(String(state.status.profile || "Default"), true);
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
    const current = String(state.status.profile || "Default");
    setProfileOpen(false);
    if (name.toLowerCase() === current.toLowerCase()) return;
    if (state.status.profileDirty === true) {
      showConfirmDialog(
        "Discard profile changes?",
        "Unsaved changes in " + current + " will be discarded before loading " + name + ".",
        "Discard",
        () => postProfileMutation("profile-select", { name, discard: true, draftDirty: true })
      );
      return;
    }
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

  function handleAction(action) {
    if (action === "close-modal") return closeDialog();
    if (action === "start") return sendSettingsThen("start");
    if (action === "scan") return sendSettingsThen("scan");
    if (action === "test") return post("test");
    if (action === "resolution") return sendSettingsThen("resolution");
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
        () => postProfileMutation("profile-delete", { name, discard: draftDirty, draftDirty })
      );
      return;
    }
    if (action === "apply") return sendSettingsThen("apply");
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
      if (["Autoblock", "Legit", "Parry", "Crushing", "Deflect", "BulwarkFallback", "YourHero", "Nohero", "Blackprior", "Nuxia", "Unblockables"].includes(control.dataset.setting)) syncLegitChanceControl();
      markProfileDirty();
      if (control.type === "checkbox") sendSettings();
    });
  });
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
    const profileName = String(state.status.profile || "Default");
    const draftDirty = state.status.profileDirty === true;
    if (!draftDirty) return postProfileMutation("profile-load", { discard: false, draftDirty: false });
    showConfirmDialog(
      "Discard profile changes?",
      "Unsaved changes in " + profileName + " will be discarded and the saved profile will be loaded.",
      "Discard",
      () => postProfileMutation("profile-load", { discard: true, draftDirty: true })
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
        applySettings(message.settings);
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