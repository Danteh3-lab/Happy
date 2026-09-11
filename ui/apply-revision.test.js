const assert = require("node:assert/strict");
const { create } = require("./apply-revision.js");

const tracker = create();
tracker.markEdit();
tracker.markTimingEdit();
tracker.begin("apply-1");
tracker.acknowledge({ Left: "70" });
tracker.markEdit();
tracker.markTimingEdit();
let result = tracker.complete("apply-1", true);
assert.equal(result.hasNewerEdits, true);
assert.equal(result.hasNewerTimingEdits, true);
assert.equal(result.shouldHydrate, false);
assert.equal(result.shouldClearTimingDirty, false);

const cleanTracker = create();
cleanTracker.begin("apply-2");
cleanTracker.acknowledge({ Left: "70" });
result = cleanTracker.complete("apply-2", true);
assert.equal(result.shouldHydrate, true);
assert.equal(result.shouldClearTimingDirty, true);

const controlOnlyTracker = create();
controlOnlyTracker.begin("apply-3");
controlOnlyTracker.markEdit();
result = controlOnlyTracker.complete("apply-3", true);
assert.equal(result.hasNewerEdits, true);
assert.equal(result.hasNewerTimingEdits, false);
assert.equal(result.shouldHydrate, false);
assert.equal(result.shouldClearTimingDirty, true);

console.log("Apply revision tests passed.");
