// Wire names shared with App/Protocol/UiMessageTypes.cs (ProtocolTests keeps them in sync).

export const PROTOCOL_VERSION = 1;

// Page → host
export const MSG = Object.freeze({
  READY: "ready",
  SETTINGS: "settings",
  START: "start",
  TOGGLE_PAUSE: "toggle-pause",
  RESOLUTION: "resolution",
  SCAN: "scan",
  TEST: "test",
  APPLY: "apply",
  PROFILE_SELECT: "profile-select",
  PROFILE_LOAD: "profile-load",
  PROFILE_SAVE: "profile-save",
  PROFILE_SAVE_AS: "profile-save-as",
  PROFILE_DELETE: "profile-delete",
  HOWTO: "howto",
  README: "readme",
  RELOAD: "reload",
  MINIMIZE: "minimize",
  CLOSE: "close",
  DRAG: "drag",
  ORANGE_PARRY: "orange-parry",
  VISION_OVERLAY: "vision-overlay",
  ANCHOR_SCAN: "anchor-scan",
  BIND_AUTO_DODGE: "bind-auto-dodge",
  BIND_ORANGE_PARRY: "bind-orange-parry",
  BIND_AUTO_PARRY: "bind-auto-parry",
  TELEMETRY: "telemetry",
  EXPORT_TELEMETRY: "export-telemetry",
  CLOUD_SIGN_IN: "cloud-sign-in",
  CLOUD_SIGN_UP: "cloud-sign-up",
  CLOUD_SIGN_OUT: "cloud-sign-out",
  CLOUD_SYNC: "cloud-sync",
});

// Host → page
export const HOST = Object.freeze({
  INIT: "init",
  STATUS: "status",
  SETTINGS: "settings",
  APPLY_RESULT: "apply-result",
  TOAST: "toast",
  DIALOG: "dialog",
});
