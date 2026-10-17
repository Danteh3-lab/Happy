import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { deriveView } from "../js/model/view-model.js";

test("run state covers standby, live, paused and error", () => {
  assert.equal(deriveView({}, {}, {}).runState, "standby");
  assert.equal(deriveView({}, { running: true }, {}).runState, "live");
  assert.equal(deriveView({}, { running: true, paused: true }, {}).runState, "paused");
  const error = deriveView({}, { running: true, error: "Capture failed" }, {});
  assert.equal(error.runState, "error");
  assert.equal(error.runCopy, "Capture failed");
});

test("start and apply controls follow run and pending state", () => {
  const live = deriveView({}, { running: true, timingsDirty: true }, { applyPending: false });
  assert.equal(live.startDisabled, true);
  assert.equal(live.pauseDisabled, false);
  assert.equal(live.startNote, false);
  const idle = deriveView({}, { timingsDirty: true }, { applyPending: false });
  assert.equal(idle.startNote, true);
  assert.equal(idle.applyDisabled, false);
  assert.equal(idle.timingState, "warn");
  const applying = deriveView({}, { timingsDirty: true }, { applyPending: true });
  assert.equal(applying.timingState, "busy");
  assert.equal(applying.applyDisabled, true);
  assert.equal(applying.startDisabled, true);
});

test("license gates start and shows in readiness and the account card", () => {
  const signedOut = deriveView({}, { cloud: { signedIn: false, license: { active: false, state: "unchecked", summary: "Sign in" } } }, {});
  assert.equal(signedOut.startDisabled, true);
  assert.equal(signedOut.readyLicenseState, "bad");
  assert.equal(signedOut.readyLicenseDetail, "Sign in");
  assert.equal(signedOut.readyFixLicense, true);
  assert.equal(signedOut.licenseNeedsKey, false);

  const noKey = deriveView({}, { cloud: { signedIn: true, license: { active: false, state: "none", summary: "No license" } } }, {});
  assert.equal(noKey.licenseNeedsKey, true);
  assert.equal(noKey.licenseState, "warn");
  const checking = deriveView({}, { cloud: { signedIn: true, license: { active: false, state: "unchecked" } } }, {});
  assert.equal(checking.licenseNeedsKey, false);

  const active = deriveView({}, { cloud: { signedIn: true, license: { active: true, state: "active", summary: "Lifetime license" } } }, {});
  assert.equal(active.startDisabled, false);
  assert.equal(active.readyLicenseState, "ok");
  assert.equal(active.licenseSummary, "Lifetime license");
  assert.equal(active.licenseNeedsKey, false);

  assert.equal(deriveView({}, {}, {}).startDisabled, false, "a payload without license data leaves the gate to the host");
});

test("license countdown shows in readiness and the account card", () => {
  const lifetime = deriveView({}, { cloud: { signedIn: true, license: { active: true, summary: "Lifetime license" } } }, {});
  assert.equal(lifetime.readyLicenseDetail, "Lifetime license");

  const timed = deriveView({}, { cloud: { signedIn: true,
    license: { active: true, summary: "Licensed until 16 Nov 2026", remaining: "29 days left", expiring: false } } }, {});
  assert.equal(timed.readyLicenseDetail, "Licensed · 29 days left");
  assert.equal(timed.readyLicenseState, "ok");
  assert.equal(timed.licenseSummary, "Licensed until 16 Nov 2026 · 29 days left");
  assert.equal(timed.licenseState, "ok");

  const soon = deriveView({}, { cloud: { signedIn: true, license: { active: true, summary: "Lifetime license",
    confirmWithin: "Offline · confirm within 20 h", expiring: true } } }, {});
  assert.equal(soon.readyLicenseDetail, "Lifetime license · Offline · confirm within 20 h");
  assert.equal(soon.readyLicenseState, "warn");
  assert.equal(soon.licenseState, "warn");
  assert.equal(soon.startDisabled, false, "a license that runs out soon still allows Start");
});

test("last reaction confirmation maps to a state", () => {
  const vm = deriveView({}, { lastReaction: { state: "PARRY SENT", confirmation: "INPUT SENT", delayMs: 60, direction: "LEFT" } }, {});
  assert.equal(vm.lastConfirmationState, "warn");
  assert.equal(vm.lastDelay, "60 ms");
  assert.equal(deriveView({}, {}, {}).lastState, "No reaction yet");
});

test("bind capture replaces the label", () => {
  const vm = deriveView({}, { autoDodgeBind: "LB", bindingOrangeParry: true }, {});
  assert.equal(vm.bindAutoDodge, "LB");
  assert.equal(vm.bindAutoDodgeState, "ok");
  assert.equal(vm.bindOrangeParryCapturing, true);
  assert.equal(vm.bindOrangeParryState, "busy");
  assert.equal(vm.bindAutoParry, "UNBOUND");
});

test("every binding key used in the markup is produced by the view model", () => {
  const html = readFileSync(new URL("../index.html", import.meta.url), "utf8");
  const vm = deriveView({}, {}, {});
  const keys = [...html.matchAll(/data-bind(?:-state|-on|-disabled|-show|-hide)?="([^"]+)"/g)].map((m) => m[1]);
  assert.ok(keys.length > 50);
  for (const key of new Set(keys)) assert.ok(key in vm, "view model is missing " + key);
});
