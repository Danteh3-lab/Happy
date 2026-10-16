import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { MSG } from "../js/protocol.js";

const read = (path) => readFileSync(new URL(path, import.meta.url), "utf8");
const html = read("../index.html");
const main = read("../js/main.js");

const BINDS = [
  ["auto-dodge-bind-button", "bind-auto-dodge", "BIND_AUTO_DODGE", "bindAutoDodgeCapturing"],
  ["orange-parry-bind-button", "bind-orange-parry", "BIND_ORANGE_PARRY", "bindOrangeParryCapturing"],
  ["auto-parry-bind-button", "bind-auto-parry", "BIND_AUTO_PARRY", "bindAutoParryCapturing"],
];

test("each bind button exists, posts its message and exposes capture state", () => {
  for (const [id, action, constant, captureKey] of BINDS) {
    const button = html.match(new RegExp('<button[^>]*id="' + id + '"[^>]*>'))?.[0];
    assert.ok(button, id + " must exist");
    assert.ok(button.includes('data-action="' + action + '"'), id + " must trigger " + action);
    assert.ok(button.includes('data-bind-on="' + captureKey + '"'), id + " must expose capture via aria-pressed");
    assert.equal(MSG[constant], action);
    assert.ok(main.includes('"' + action + '": () => send(MSG.' + constant + ")"), action + " must post to the host");
  }
});

test("bound buttons show their current assignment", () => {
  for (const key of ["bindAutoDodge", "bindOrangeParry", "bindAutoParry"]) {
    assert.ok(html.includes('data-bind="' + key + '"'), key + " must be displayed");
  }
});
