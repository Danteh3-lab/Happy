(function () {
  "use strict";

  const bridge = window.chrome && window.chrome.webview;
  const state = { settings: {}, status: {} };
  let hydrating = false;

  const $ = (selector) => document.querySelector(selector);
  const $$ = (selector) => Array.from(document.querySelectorAll(selector));

  function post(type, extra) {
    if (!bridge) return;
    bridge.postMessage(Object.assign({ type }, extra || {}));
  }

  function sendSettings() {
    post("settings", { settings: Object.assign({}, state.settings) });
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
  }

  function updateStatus(status) {
    state.status = status || {};
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
    const loop = Number(state.status.loop || 0);
    const orangeParry = state.status.orangeParry === true;
    const visionOverlay = state.status.visionOverlay === true;
    const anchorScan = state.status.anchorScan !== false;
    const telemetry = state.status.telemetry || {};
    const telemetryRecording = telemetry.recording === true;

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
    $("#parry-count").textContent = String(state.status.parryCount || 0) + " parries · P-" + (state.status.parryToggle === false ? "OFF" : "ON");
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
  }

  function setMetric(id, text, good) {
    const element = $("#" + id);
    element.textContent = text;
    element.classList.toggle("good", Boolean(good));
    element.classList.toggle("alert", text === "MISSING" || text === "OFF" || text === "UP");
  }

  function showDialog(title, body, eyebrow) {
    $("#modal-title").textContent = title || "Message";
    $("#modal-eyebrow").textContent = eyebrow || "MESSAGE";
    $("#modal-body").textContent = body || "";
    $("#modal-root").hidden = false;
  }

  function closeDialog() {
    $("#modal-root").hidden = true;
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
    if (action === "start") return post("start");
    if (action === "scan") return post("scan");
    if (action === "test") return post("test");
    if (action === "resolution") return post("resolution");
    if (action === "save") return post("save");
    if (action === "load") return post("load");
    if (action === "apply") return post("apply");
    if (action === "howto") return post("howto");
    if (action === "readme") return post("readme");
    if (action === "reload") return post("reload");
    if (action === "minimize") return post("minimize");
    if (action === "close") return post("close");
    if (action === "drag") return post("drag");
    if (action === "toggle-orange-parry") return post("orange-parry");
    if (action === "vision-overlay") return post("vision-overlay");
    if (action === "anchor-scan") return post("anchor-scan");
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
      sendSettings();
    });
  });
  $(".titlebar").addEventListener("pointerdown", (event) => {
    if (!event.target.closest("button")) post("drag");
  });
  $("#modal-root").addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeDialog();
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
