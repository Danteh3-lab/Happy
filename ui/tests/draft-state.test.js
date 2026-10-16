import test from "node:test";
import assert from "node:assert/strict";
import { createDraftState } from "../js/model/draft-state.js";

const ids = () => { let n = 0; return () => "req-" + (++n); };

test("a profile mutation clears the local flag only on a clean host status", () => {
  const draft = createDraftState({ idFactory: ids() });
  draft.markProfileEdit();
  assert.equal(draft.effectiveStatus({ profileDirty: false }).profileDirty, true);

  draft.observeHostStatus({ profileDirty: false });
  assert.equal(draft.profileDraftDirty, true, "a clean status before the mutation must not clear edits");

  draft.beginProfileMutation(false);
  draft.observeHostStatus({ profileDirty: true });
  assert.equal(draft.profileDraftDirty, true);
  draft.observeHostStatus({ profileDirty: false });
  assert.equal(draft.effectiveStatus({ profileDirty: false }).profileDirty, false);
});

test("number edits stay local and survive unrelated settings messages", () => {
  const draft = createDraftState({ idFactory: ids() });
  draft.markTimingEdit("ParryDelay");
  assert.equal(draft.effectiveStatus({ timingsDirty: false }).timingsDirty, true);
  const keep = draft.onSettings({ ParryDelay: 10 });
  assert.deepEqual([...keep], ["ParryDelay"]);
});

test("a profile load discards local timing edits", () => {
  const draft = createDraftState({ idFactory: ids() });
  draft.markTimingEdit("Pause");
  draft.beginProfileMutation(true);
  assert.deepEqual([...draft.onSettings({})], []);
  assert.equal(draft.timingDraftDirty, false);
  assert.equal(draft.profileLoadPending, false);
});

test("apply acknowledges settings, then hydrates and clears on success", () => {
  const draft = createDraftState({ idFactory: ids() });
  draft.markTimingEdit("Left");
  const id = draft.beginApply();
  assert.equal(draft.applyPending, true);
  assert.equal(draft.onSettings({ Left: 70 }), null, "settings during apply are held as the acknowledgement");
  assert.equal(draft.completeApply({ requestId: "other", success: true }), null);
  const result = draft.completeApply({ requestId: id, success: true });
  assert.deepEqual(result, { success: true, hydrateWith: { Left: 70 }, newerTimingEditsPending: false });
  assert.equal(draft.applyPending, false);
  assert.equal(draft.timingDraftDirty, false);
});

test("timing edits during apply stay pending", () => {
  const draft = createDraftState({ idFactory: ids() });
  draft.markTimingEdit("Left");
  const id = draft.beginApply();
  draft.markProfileEdit();
  draft.markTimingEdit("Right");
  const result = draft.completeApply({ requestId: id, success: true });
  assert.equal(result.hydrateWith, null);
  assert.equal(result.newerTimingEditsPending, true);
  assert.equal(draft.timingDraftDirty, true);
});

test("a failed apply keeps timings pending", () => {
  const draft = createDraftState({ idFactory: ids() });
  draft.markTimingEdit("Left");
  const id = draft.beginApply();
  const result = draft.completeApply({ requestId: id, success: false });
  assert.equal(result.success, false);
  assert.equal(draft.timingDraftDirty, true);
});
