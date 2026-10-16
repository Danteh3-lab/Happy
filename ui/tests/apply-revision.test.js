import test from "node:test";
import assert from "node:assert/strict";
import { create } from "../js/model/apply-revision.js";

test("newer edits block hydration and keep timings pending", () => {
  const tracker = create();
  tracker.markEdit();
  tracker.markTimingEdit();
  tracker.begin("apply-1");
  tracker.acknowledge({ Left: "70" });
  tracker.markEdit();
  tracker.markTimingEdit();
  const result = tracker.complete("apply-1", true);
  assert.equal(result.hasNewerEdits, true);
  assert.equal(result.hasNewerTimingEdits, true);
  assert.equal(result.shouldHydrate, false);
  assert.equal(result.shouldClearTimingDirty, false);
});

test("a clean apply hydrates and clears timings", () => {
  const tracker = create();
  tracker.begin("apply-2");
  tracker.acknowledge({ Left: "70" });
  const result = tracker.complete("apply-2", true);
  assert.equal(result.shouldHydrate, true);
  assert.equal(result.shouldClearTimingDirty, true);
});

test("a switch edit during apply blocks hydration but not timing clear", () => {
  const tracker = create();
  tracker.begin("apply-3");
  tracker.markEdit();
  const result = tracker.complete("apply-3", true);
  assert.equal(result.hasNewerEdits, true);
  assert.equal(result.hasNewerTimingEdits, false);
  assert.equal(result.shouldHydrate, false);
  assert.equal(result.shouldClearTimingDirty, true);
});

test("results for other requests are ignored", () => {
  const tracker = create();
  tracker.begin("apply-4");
  assert.deepEqual(tracker.complete("other", true), { ignored: true });
});
