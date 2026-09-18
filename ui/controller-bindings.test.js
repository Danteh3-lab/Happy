const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const html = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");
const app = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");

for (const binding of [
  ["auto-dodge-bind-button", "bind-auto-dodge"],
  ["orange-parry-bind-button", "bind-orange-parry"],
  ["auto-parry-bind-button", "bind-auto-parry"],
]) {
  const [id, action] = binding;
  assert.ok(html.includes('id="' + id + '"'), id + " must exist in diagnostics");
  assert.ok(html.includes('data-action="' + action + '"'), action + " must be exposed as an action");
  assert.ok(app.includes('post("' + action + '")'), action + " must post to the controller host");
}

assert.ok(app.includes('renderBindingButton("orange-parry-bind-button"'), "Orange parry bind status must render");
assert.ok(app.includes('renderBindingButton("auto-parry-bind-button"'), "Auto parry bind status must render");
assert.ok(app.includes('setAttribute("aria-pressed"'), "capture state must be exposed to assistive technology");

console.log("Controller binding UI tests passed.");
