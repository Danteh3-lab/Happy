const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const dodge = require("./dodge-direction.js");

function fakeButton(value) {
  const classes = new Set();
  const attrs = {};
  return {
    dataset: { dodge: value },
    tabIndex: -1,
    classList: {
      toggle(name, force) {
        if (force) classes.add(name);
        else classes.delete(name);
      },
      contains(name) {
        return classes.has(name);
      },
    },
    setAttribute(key, val) {
      attrs[key] = String(val);
    },
    getAttribute(key) {
      return attrs[key];
    },
    _classes: classes,
    _attrs: attrs,
  };
}

function hydratedButtons(settings) {
  const normalized = dodge.normalize(Object.assign({}, settings));
  const buttons = [fakeButton("back"), fakeButton("left"), fakeButton("right")];
  dodge.sync(buttons, dodge.fromSettings(normalized));
  return { normalized, buttons };
}

function selectedValue(buttons) {
  const active = buttons.filter((button) => button._classes.has("active"));
  assert.equal(active.length, 1, "exactly one radio must be active");
  return active[0].dataset.dodge;
}

// Back maps to both flags false.
assert.equal(dodge.fromSettings({ Leftdodge: false, Rightdodge: false }), "back");
assert.deepEqual(dodge.toSettings("back"), { Leftdodge: false, Rightdodge: false });

// Left and Right remain mutually exclusive.
assert.equal(dodge.fromSettings({ Leftdodge: true, Rightdodge: false }), "left");
assert.equal(dodge.fromSettings({ Leftdodge: false, Rightdodge: true }), "right");
assert.deepEqual(dodge.toSettings("left"), { Leftdodge: true, Rightdodge: false });
assert.deepEqual(dodge.toSettings("right"), { Leftdodge: false, Rightdodge: true });

// Invalid legacy data where both values are true normalizes to Right.
assert.equal(dodge.fromSettings({ Leftdodge: true, Rightdodge: true }), "right");
assert.deepEqual(
  dodge.normalize({ Leftdodge: true, Rightdodge: true }),
  { Leftdodge: false, Rightdodge: true },
);

// Profile hydration selects the correct UI option, including roving focus.
for (const [settings, expected] of [
  [{ Leftdodge: false, Rightdodge: false }, "back"],
  [{ Leftdodge: true, Rightdodge: false }, "left"],
  [{ Leftdodge: false, Rightdodge: true }, "right"],
  [{ Leftdodge: true, Rightdodge: true }, "right"],
]) {
  const { buttons } = hydratedButtons(settings);
  assert.equal(selectedValue(buttons), expected, "hydration must select " + expected);
  for (const button of buttons) {
    const isSelected = button.dataset.dodge === expected;
    assert.equal(button.getAttribute("aria-checked"), isSelected ? "true" : "false");
    assert.equal(button.tabIndex, isSelected ? 0 : -1, "roving tabindex must leave only " + expected + " tabbable");
  }
}

// Roving keyboard model: arrows move and wrap, Home/End jump.
assert.equal(dodge.keyToDelta("ArrowRight"), 1);
assert.equal(dodge.keyToDelta("ArrowDown"), 1);
assert.equal(dodge.keyToDelta("ArrowLeft"), -1);
assert.equal(dodge.keyToDelta("ArrowUp"), -1);
assert.equal(dodge.step("back", 1), "left");
assert.equal(dodge.step("left", 1), "right");
assert.equal(dodge.step("right", 1), "back");
assert.equal(dodge.step("back", -1), "right");
assert.deepEqual(dodge.ORDER, ["back", "left", "right"]);

// Static markup keeps a single-tab-stop radiogroup.
const html = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");
assert.match(html, /role="radiogroup"/, "dodge control must expose a radiogroup");
for (const value of ["back", "left", "right"]) {
  assert.ok(html.includes('data-dodge="' + value + '"'), "radiogroup must offer " + value);
}
const backTab = html.match(/data-dodge="back"[^>]*tabindex="([^"]+)"/i) ||
  html.match(/tabindex="([^"]+)"[^>]*data-dodge="back"/i);
const leftTab = html.match(/data-dodge="left"[^>]*tabindex="([^"]+)"/i) ||
  html.match(/tabindex="([^"]+)"[^>]*data-dodge="left"/i);
const rightTab = html.match(/data-dodge="right"[^>]*tabindex="([^"]+)"/i) ||
  html.match(/tabindex="([^"]+)"[^>]*data-dodge="right"/i);
assert.equal(backTab && backTab[1], "0", "initial Back option must be the single tab stop");
assert.equal(leftTab && leftTab[1], "-1", "initial Left option must be skipped by Tab");
assert.equal(rightTab && rightTab[1], "-1", "initial Right option must be skipped by Tab");

// App wiring keeps the roving model and full arrow/Home/End handling.
const app = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
assert.ok(app.includes("HappyDodgeDirection"), "app must use the shared dodge-direction module");
assert.ok(app.includes("tabIndex"), "app sync must maintain roving tabindex");
assert.ok(app.includes("ArrowUp") && app.includes("ArrowDown"), "radiogroup must handle vertical arrows");
assert.ok(app.includes('"Home"') || app.includes("'Home'"), "radiogroup must handle Home");
assert.ok(app.includes('"End"') || app.includes("'End'"), "radiogroup must handle End");

console.log("Dodge direction tests passed.");
