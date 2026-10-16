import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { MSG, HOST } from "../js/protocol.js";

const read = (path) => readFileSync(new URL(path, import.meta.url), "utf8");

test("message names are unique", () => {
  const values = Object.values(MSG);
  assert.equal(new Set(values).size, values.length);
  assert.equal(new Set(Object.values(HOST)).size, Object.values(HOST).length);
});

test("every page message is sent somewhere and every host message is handled", () => {
  const main = read("../js/main.js");
  // howto/readme are host-side dialogs kept for older pages; the deck shows its own Help panel.
  const hostOnly = new Set(["HOWTO", "README"]);
  for (const key of Object.keys(MSG)) {
    if (hostOnly.has(key)) continue;
    assert.ok(main.includes("MSG." + key), "MSG." + key + " is never sent");
  }
  for (const key of Object.keys(HOST)) {
    assert.ok(main.includes("HOST." + key), "HOST." + key + " is never handled");
  }
});
