// Control deck boot: wires the static markup in index.html to the host bridge.
import { MSG, HOST, PROTOCOL_VERSION } from "./protocol.js";
import { createBridge } from "./bridge.js";
import { $, $$, h, setText } from "./dom.js";
import { createRenderer } from "./render.js";
import { createDraftState } from "./model/draft-state.js";
import { deriveView } from "./model/view-model.js";
import { heroGroups, heroLabel } from "./model/hero-catalog.js";
import { fromSettings as dodgeFromSettings, toSettings as dodgeToSettings } from "./model/dodge-direction.js";
import {
  AVAILABILITY_INPUTS, controlAvailability, heroPatch, normalizeSettings, resolutionMessage, selectedHero,
  validationMessage,
} from "./model/settings-model.js";
import { createListbox } from "./components/listbox.js";
import { createDodgeSegmented } from "./components/segmented.js";
import { createModal } from "./components/modal.js";
import { createToaster } from "./components/toast.js";
import { createMenuButton } from "./components/menu-button.js";
import { createNav } from "./components/nav.js";
import { createHelpPanel } from "./components/help-panel.js";

const bridge = createBridge();
const draft = createDraftState();
// hostStatus is what the bot last reported; status folds in local edit flags.
const state = { settings: {}, hostStatus: {}, status: {} };

const renderer = createRenderer(document);
const toast = createToaster($("#toast-stack"));
const modal = createModal($("#modal-root"));
const nav = createNav();
const help = createHelpPanel($("#help-panel"));
const settingControls = $$("[data-setting]");
const numericControls = settingControls.filter((control) => control.type === "number");

let hydrating = false;

// ---------------------------------------------------------------- sending

function send(type, extra) {
  bridge.send(type, extra);
}

function sendSettings() {
  send(MSG.SETTINGS, { settings: Object.assign({}, state.settings) });
}

function sendSettingsThen(type, extra) {
  sendSettings();
  send(type, extra);
}

function profileMutation(type, extra, loadsSettings) {
  draft.beginProfileMutation(loadsSettings);
  send(type, extra);
}

// ---------------------------------------------------------------- settings controls

function hydrateControls() {
  hydrating = true;
  const s = state.settings;
  settingControls.forEach((control) => {
    const key = control.dataset.setting;
    const value = s[key];
    if (value === undefined) return;
    if (control.type === "checkbox") {
      const checked = key === "YourHero" ? Boolean(value) && s.Nohero !== true : Boolean(value);
      if (control.checked !== checked) control.checked = checked;
    } else if (document.activeElement !== control || control.value === "") {
      if (control.value !== String(value)) control.value = value;
    }
  });
  hydrating = false;
  syncAvailability();
  dodge.update(dodgeFromSettings(s));
  heroPicker.update(heroOptionGroups, selectedHero(s));
}

function syncAvailability() {
  const available = controlAvailability(state.settings);
  for (const [key, enabled] of Object.entries(available)) {
    $$('[data-setting="' + key + '"]').forEach((control) => {
      control.disabled = !enabled;
      control.closest(".switch-row, .switch-tile, .field")?.classList.toggle("is-disabled", !enabled);
    });
  }
}

function applyIncomingSettings(settings, keepLocal) {
  const incoming = normalizeSettings(settings);
  if (keepLocal) keepLocal.forEach((key) => { if (key in state.settings) incoming[key] = state.settings[key]; });
  state.settings = Object.assign({}, state.settings, incoming);
  hydrateControls();
}

function onSettingChanged(control) {
  if (hydrating) return;
  const key = control.dataset.setting;
  if (control.type === "checkbox") {
    state.settings[key] = control.checked;
    if (key === "YourHero") state.settings.Nohero = !control.checked;
    draft.markProfileEdit();
    // Mirror the change onto duplicate copies (quick toggles) and dependents.
    hydrateControls();
    if (AVAILABILITY_INPUTS.has(key)) syncAvailability();
    sendSettings();
  } else {
    state.settings[key] = control.value;
    validateControl(control);
    draft.markProfileEdit();
    draft.markTimingEdit(key);
  }
  render();
}

settingControls.forEach((control) => {
  control.addEventListener(control.type === "checkbox" ? "change" : "input", () => onSettingChanged(control));
});

// ---------------------------------------------------------------- validation

function setFieldError(control, message) {
  const key = control.dataset.setting;
  const error = $('[data-error-for="' + key + '"]');
  if (error) setText(error, message);
  control.closest(".field")?.classList.toggle("is-invalid", Boolean(message));
  control.setAttribute("aria-invalid", message ? "true" : "false");
  control.setCustomValidity(message);
}

function validateControl(control) {
  const message = validationMessage(control.value, Number(control.min), Number(control.max));
  setFieldError(control, message);
  if (!message && (control.dataset.setting === "res1" || control.dataset.setting === "res2")) {
    const width = $('[data-setting="res1"]');
    const height = $('[data-setting="res2"]');
    const resMessage = resolutionMessage(width.value, height.value);
    setFieldError(width, resMessage || validationMessage(width.value, 1, 16384));
    return !resMessage;
  }
  return !message;
}

// Returns the first invalid control, or null.
function validateAll() {
  let firstInvalid = null;
  numericControls.forEach((control) => {
    if (!validateControl(control) && !firstInvalid) firstInvalid = control;
  });
  if (!firstInvalid) {
    const width = $('[data-setting="res1"]');
    if (width && width.getAttribute("aria-invalid") === "true") firstInvalid = width;
  }
  return firstInvalid;
}

function rejectInvalid(control, message) {
  toast(message, "error");
  const page = nav.pageOf(control);
  if (page && page !== nav.current) nav.select(page);
  control.focus();
}

// ---------------------------------------------------------------- pickers

const heroOptionGroups = [{ options: [{ value: "", label: "None" }] }].concat(
  heroGroups.map((group) => ({ label: group.label, options: group.keys.map((key) => ({ value: key, label: heroLabel(key) })) })),
);

const heroPicker = createListbox({
  root: $("#hero-select"),
  trigger: $("#hero-select-trigger"),
  menu: $("#hero-select-menu"),
  valueLabel: $("#hero-select-value"),
  onChoose(hero) {
    Object.assign(state.settings, heroPatch(hero));
    draft.markProfileEdit();
    hydrateControls();
    sendSettings();
    render();
  },
});

const profilePicker = createListbox({
  root: $("#profile-select"),
  trigger: $("#profile-select-trigger"),
  menu: $("#profile-select-menu"),
  valueLabel: $("#profile-select-value"),
  onChoose: requestProfileSelection,
});

const dodge = createDodgeSegmented($("#dodge-direction"), {
  getDirection: () => dodgeFromSettings(state.settings),
  setDirection(direction) {
    Object.assign(state.settings, dodgeToSettings(direction));
    dodge.update(direction);
    draft.markProfileEdit();
    sendSettings();
    render();
  },
});

function uniqueProfiles(list) {
  const seen = new Set();
  return list.map(String).filter((name) => {
    const key = name.toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

// ---------------------------------------------------------------- profile flows

function requestProfileSelection(name) {
  if (draft.applyPending) return toast("Wait for Apply to finish before switching profiles.", "info");
  const current = String(state.status.profile || "Default");
  if (name.toLowerCase() === current.toLowerCase()) return;
  if (state.status.profileDirty === true) {
    modal.confirm("Discard profile changes?",
      "Unsaved changes in " + current + " will be discarded before loading " + name + ".",
      "Discard",
      () => profileMutation(MSG.PROFILE_SELECT, { name, discard: true, draftDirty: true }, true),
      { danger: true });
    return;
  }
  profileMutation(MSG.PROFILE_SELECT, { name, discard: false, draftDirty: false }, true);
}

function requestProfileLoad() {
  if (draft.applyPending) return toast("Wait for Apply to finish before loading a profile.", "info");
  const name = String(state.status.profile || "Default");
  if (state.status.profileDirty !== true) {
    return profileMutation(MSG.PROFILE_LOAD, { discard: false, draftDirty: false }, true);
  }
  modal.confirm("Discard profile changes?",
    "Unsaved changes in " + name + " will be discarded and the saved profile will be loaded.",
    "Discard",
    () => profileMutation(MSG.PROFILE_LOAD, { discard: true, draftDirty: true }, true),
    { danger: true });
}

function requestSaveAs() {
  modal.prompt("Save profile as",
    "Create a named profile. Use letters, numbers, spaces, dashes, or underscores.",
    "Profile name", state.status.profile || "", "Save",
    (name) => {
      if (!name) return toast("Enter a profile name.", "error");
      const profiles = Array.isArray(state.status.profiles) ? state.status.profiles : [];
      const save = () => {
        sendSettings();
        profileMutation(MSG.PROFILE_SAVE_AS, { name }, false);
      };
      if (profiles.some((profile) => String(profile).toLowerCase() === name.toLowerCase())) {
        modal.confirm("Overwrite profile?",
          "A profile named " + name + " already exists. Replace its saved settings?", "Overwrite", save, { danger: true });
      } else {
        save();
      }
    });
}

function requestDelete() {
  const name = String(state.status.profile || "Default");
  if (name.toLowerCase() === "default") return toast("The Default profile cannot be deleted.", "error");
  const draftDirty = state.status.profileDirty === true;
  modal.confirm("Delete profile?",
    "Delete " + name + " and its saved settings?" + (draftDirty ? " Unsaved changes will also be discarded." : "") +
      " This cannot be undone.",
    "Delete",
    () => profileMutation(MSG.PROFILE_DELETE, { name, discard: draftDirty, draftDirty }, true),
    { danger: true });
}

// ---------------------------------------------------------------- apply / start

function beginAcknowledgedApply(type) {
  const requestId = draft.beginApply();
  render();
  sendSettings();
  send(type, { requestId });
}

// ---------------------------------------------------------------- actions

const actions = {
  start() {
    const invalid = validateAll();
    if (invalid) return rejectInvalid(invalid, "Fix the highlighted values before starting the bot.");
    beginAcknowledgedApply(MSG.START);
  },
  apply() {
    const invalid = validateAll();
    if (invalid) return rejectInvalid(invalid, "Fix the highlighted values before applying timings.");
    beginAcknowledgedApply(MSG.APPLY);
  },
  "toggle-pause": () => send(MSG.TOGGLE_PAUSE),
  scan: () => sendSettingsThen(MSG.SCAN),
  test: () => send(MSG.TEST),
  resolution() {
    const invalid = ["res1", "res2"].map((key) => $('[data-setting="' + key + '"]')).find((control) => !validateControl(control));
    if (invalid) return rejectInvalid(invalid, "Fix the highlighted values before applying calibration.");
    sendSettingsThen(MSG.RESOLUTION);
  },
  "profile-save": () => {
    sendSettings();
    profileMutation(MSG.PROFILE_SAVE, undefined, false);
  },
  "profile-save-as": requestSaveAs,
  "profile-load": requestProfileLoad,
  "profile-delete": requestDelete,
  help: (target) => {
    const section = target && target.dataset.helpSection;
    if (section) help.open(section);
    else help.toggle();
  },
  reload: () => send(MSG.RELOAD),
  minimize: () => send(MSG.MINIMIZE),
  close: () => send(MSG.CLOSE),
  "orange-parry": () => send(MSG.ORANGE_PARRY),
  "vision-overlay": () => send(MSG.VISION_OVERLAY),
  "anchor-scan": () => send(MSG.ANCHOR_SCAN),
  "bind-auto-dodge": () => send(MSG.BIND_AUTO_DODGE),
  "bind-orange-parry": () => send(MSG.BIND_ORANGE_PARRY),
  "bind-auto-parry": () => send(MSG.BIND_AUTO_PARRY),
  telemetry: () => send(MSG.TELEMETRY, { label: $("#telemetry-label").value }),
  "export-telemetry": () => send(MSG.EXPORT_TELEMETRY),
  "cloud-sign-in": () => sendCloudCredentials(MSG.CLOUD_SIGN_IN),
  "cloud-sign-up": () => sendCloudCredentials(MSG.CLOUD_SIGN_UP),
  "cloud-sign-out": () => send(MSG.CLOUD_SIGN_OUT),
  "cloud-sync": () => send(MSG.CLOUD_SYNC),
  "license-redeem": () => {
    const field = $("#license-key");
    const key = field.value.trim();
    if (!key) {
      toast("Enter a license key.", "error");
      return;
    }
    send(MSG.LICENSE_REDEEM, { key });
    field.value = "";
  },
};

// The password goes straight to the host (which sends it to Supabase over
// HTTPS) and is cleared from the page; only a refresh token is ever stored.
function sendCloudCredentials(type) {
  const email = $("#cloud-email").value.trim();
  const passwordField = $("#cloud-password");
  const password = passwordField.value;
  if (!email || !password) {
    toast("Enter an email and password.", "error");
    return;
  }
  send(type, { email, password });
  passwordField.value = "";
}

// Sign in runs on submit, so Enter and the button take the same path; the
// browser never navigates.
$("#cloud-form").addEventListener("submit", (event) => {
  event.preventDefault();
  actions["cloud-sign-in"]();
});

$("#license-form").addEventListener("submit", (event) => {
  event.preventDefault();
  actions["license-redeem"]();
});

document.addEventListener("click", (event) => {
  const target = event.target.closest("[data-action]");
  if (!target || target.disabled) return;
  const handler = actions[target.dataset.action];
  if (!handler) return;
  event.preventDefault();
  handler(target);
});

createMenuButton($("#profile-menu"), $("#profile-menu-trigger"), $("#profile-menu-list"), (action) => {
  const handler = actions[action];
  if (handler) handler();
});

$(".titlebar").addEventListener("pointerdown", (event) => {
  if (event.button === 0 && !event.target.closest("button, a, input, select")) send(MSG.DRAG);
});

// ---------------------------------------------------------------- rendering

function renderNotices(vm) {
  $$("[data-notices]").forEach((root) => {
    const next = vm.notices.join("\n");
    if (root.dataset.rendered === next) return;
    root.dataset.rendered = next;
    root.replaceChildren(...vm.notices.map((notice) => h("li", { class: "notice" }, notice)));
    root.hidden = vm.notices.length === 0;
  });
}

function render() {
  state.status = draft.effectiveStatus(state.hostStatus);
  const vm = deriveView(state.settings, state.status, { applyPending: draft.applyPending });
  renderer.apply(vm);
  renderNotices(vm);
  const profiles = Array.isArray(state.status.profiles) && state.status.profiles.length
    ? uniqueProfiles(state.status.profiles)
    : ["Default"];
  profilePicker.update([{ options: profiles.map((name) => ({ value: name, label: name })) }], vm.profileName);
  document.documentElement.dataset.run = vm.runState;
}

// ---------------------------------------------------------------- host messages

bridge.onMessage((message) => {
  switch (message.type) {
    case HOST.INIT:
      if (message.protocol != null && message.protocol !== PROTOCOL_VERSION) {
        toast("The control deck and the bot are different versions. Restart DANBOT.", "error");
      }
      applyIncomingSettings(message.settings, null);
      state.hostStatus = message.status || {};
      draft.observeHostStatus(state.hostStatus);
      render();
      break;
    case HOST.STATUS:
      state.hostStatus = message.status || message;
      draft.observeHostStatus(state.hostStatus);
      render();
      break;
    case HOST.SETTINGS: {
      const keep = draft.onSettings(message.settings);
      if (keep) applyIncomingSettings(message.settings, keep);
      render();
      break;
    }
    case HOST.APPLY_RESULT: {
      const result = draft.completeApply(message);
      if (!result) return;
      if (result.hydrateWith) applyIncomingSettings(result.hydrateWith, null);
      if (result.newerTimingEditsPending) toast("Earlier timings applied; newer edits are still pending.", "warning");
      render();
      break;
    }
    case HOST.TOAST:
      toast(message.message, message.kind);
      break;
    case HOST.DIALOG:
      modal.message(message.title, message.body, message.eyebrow);
      break;
  }
});

render();
if (bridge.available) send(MSG.READY);
else toast("Bot connection unavailable — open the deck from DANBOT.", "error");
