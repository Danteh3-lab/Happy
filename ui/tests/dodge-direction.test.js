import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import * as dodge from "../js/model/dodge-direction.js";

const read = (path) => readFileSync(new URL(path, import.meta.url), "utf8");

function fakeButton(value) {
  const classes = new Set();
  const attrs = {};
  return {
    dataset: { dodge: value },
    tabIndex: -1,
    classList: { toggle(name, force) { if (force) classes.add(name); else classes.delete(name); } },
    setAttribute(key, val) { attrs[key] = String(val); },
    getAttribute(key) { return attrs[key]; },
    classes,
  };
}

test("back, left and right map to exclusive flags", () => {
  assert.equal(dodge.fromSettings({ Leftdodge: false, Rightdodge: false }), "back");
  assert.equal(dodge.fromSettings({ Leftdodge: true, Rightdodge: false }), "left");
  assert.equal(dodge.fromSettings({ Leftdodge: false, Rightdodge: true }), "right");
  assert.deepEqual(dodge.toSettings("back"), { Leftdodge: false, Rightdodge: false });
  assert.deepEqual(dodge.toSettings("left"), { Leftdodge: true, Rightdodge: false });
  assert.deepEqual(dodge.toSettings("right"), { Leftdodge: false, Rightdodge: true });
});

test("legacy profiles with both flags normalize to right", () => {
  assert.equal(dodge.fromSettings({ Leftdodge: true, Rightdodge: true }), "right");
  assert.deepEqual(dodge.normalize({ Leftdodge: true, Rightdodge: true }), { Leftdodge: false, Rightdodge: true });
});

test("hydration selects one option with a roving tab stop", () => {
  for (const [settings, expected] of [
    [{ Leftdodge: false, Rightdodge: false }, "back"],
    [{ Leftdodge: true, Rightdodge: false }, "left"],
    [{ Leftdodge: true, Rightdodge: true }, "right"],
  ]) {
    const buttons = ["back", "left", "right"].map(fakeButton);
    dodge.sync(buttons, dodge.fromSettings(dodge.normalize(settings)));
    for (const button of buttons) {
      const selected = button.dataset.dodge === expected;
      assert.equal(button.classes.has("active"), selected);
      assert.equal(button.getAttribute("aria-checked"), selected ? "true" : "false");
      assert.equal(button.tabIndex, selected ? 0 : -1);
    }
  }
});

test("arrow keys step and wrap", () => {
  assert.equal(dodge.keyToDelta("ArrowRight"), 1);
  assert.equal(dodge.keyToDelta("ArrowDown"), 1);
  assert.equal(dodge.keyToDelta("ArrowLeft"), -1);
  assert.equal(dodge.keyToDelta("ArrowUp"), -1);
  assert.equal(dodge.step("right", 1), "back");
  assert.equal(dodge.step("back", -1), "right");
  assert.deepEqual(dodge.ORDER, ["back", "left", "right"]);
});

test("markup is a single-tab-stop radiogroup wired to the shared model", () => {
  const html = read("../index.html");
  assert.match(html, /role="radiogroup"[^>]*id="dodge-direction"/);
  const tab = (value) => html.match(new RegExp('tabindex="(-?\\d)" data-dodge="' + value + '"'))?.[1];
  assert.equal(tab("back"), "0");
  assert.equal(tab("left"), "-1");
  assert.equal(tab("right"), "-1");

  const segmented = read("../js/components/segmented.js");
  assert.match(segmented, /from "\.\.\/model\/dodge-direction\.js"/);
  assert.ok(segmented.includes('"Home"') && segmented.includes('"End"'), "radiogroup must handle Home/End");
  assert.ok(segmented.includes("keyToDelta"), "radiogroup must handle arrow keys");
});
