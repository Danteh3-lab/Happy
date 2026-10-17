// "Nothing dropped" guard for the page layout: every setting the host
// persists has a control, and every numeric setting has exactly one.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { heroKeys } from "../js/model/hero-catalog.js";

const read = (path) => readFileSync(new URL(path, import.meta.url), "utf8");
const html = read("../index.html");
const codec = read("../../SettingsCodec.cs");
const settingsCs = read("../../Settings.cs");

function csArray(name) {
  const body = codec.match(new RegExp(name + "\\s*=\\s*\\{([\\s\\S]*?)\\};"))[1];
  return [...body.matchAll(/"([^"]+)"/g)].map((m) => m[1]);
}

const editKeys = csArray("EditKeys");
const checkKeys = csArray("CheckKeys");
const settingCounts = new Map();
for (const match of html.matchAll(/data-setting="([^"]+)"/g)) {
  settingCounts.set(match[1], (settingCounts.get(match[1]) || 0) + 1);
}

// Covered by other controls: the hero picker, the dodge-direction radiogroup,
// the orange-parry toggle (host-owned, data-action), and the bind buttons.
const coveredElsewhere = new Set([...heroKeys, "Nohero", "Leftdodge", "Rightdodge", "OrangeParry",
  "AutoDodgeBind", "OrangeParryBind", "AutoParryBind"]);

test("every persisted setting has a control", () => {
  for (const key of [...editKeys, ...checkKeys]) {
    if (coveredElsewhere.has(key)) continue;
    assert.ok(settingCounts.has(key), key + " has no data-setting control");
  }
  assert.ok(html.includes('id="hero-select"'), "hero picker must exist");
  assert.ok(html.includes('id="dodge-direction"'), "dodge direction must exist");
  assert.ok(html.includes('data-action="orange-parry"'), "orange parry toggle must exist");
});

test("each numeric setting appears exactly once", () => {
  const numeric = editKeys.filter((key) => !key.endsWith("Bind"));
  for (const key of numeric) {
    assert.equal(settingCounts.get(key), 1, key + " must appear exactly once");
  }
});

test("required actions and controls exist", () => {
  for (const action of ["start", "toggle-pause", "apply", "profile-save", "scan", "test", "resolution",
    "vision-overlay", "anchor-scan", "telemetry", "export-telemetry", "reload", "minimize", "close", "help"]) {
    assert.ok(html.includes('data-action="' + action + '"'), "missing action " + action);
  }
  for (const action of ["profile-save-as", "profile-load", "profile-delete"]) {
    assert.ok(html.includes('data-menu-action="' + action + '"'), "missing profile menu action " + action);
  }
  for (const id of ["profile-select", "timing-state", "profile-state", "apply-button", "save-profile-button",
    "telemetry-label", "start-button", "pause-button"]) {
    assert.ok(html.includes('id="' + id + '"'), "missing #" + id);
  }
});

test("hero catalog matches Settings.Chars", () => {
  const chars = settingsCs.match(/Chars = new\([\s\S]*?\{([\s\S]*?)\};/)[1];
  const csHeroes = [...chars.matchAll(/\["([^"]+)"\]/g)].map((m) => m[1]);
  assert.deepEqual([...heroKeys].sort(), [...csHeroes].sort());
});

test("no keyboard hotkeys or E hold remain", () => {
  assert.ok(!/<kbd>F[1-7]<\/kbd>/.test(html), "function-key hotkeys were removed; the DS4 is the only input");
  assert.ok(!/data-setting="(Parry2|Crushing2)"/.test(html), "the keyboard-only E hold reactions were removed");
});
