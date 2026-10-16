// Local edit bookkeeping between the page and the host.
//
// - Switches go to the host immediately; numbers stay local until Apply.
// - "Profile not saved" and "Timings not applied" are tracked separately.
// - A profile mutation (save, load, select, delete) clears the local profile
//   flag only once the host reports a clean profile.
// - Apply/Start carry a request id; apply-revision decides whether the
//   acknowledgement may hydrate the page and clear the timing flag.
import { create as createApplyRevision } from "./apply-revision.js";

function defaultId() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2);
}

export function createDraftState(options) {
  const nextId = (options && options.idFactory) || defaultId;
  const revision = createApplyRevision();
  const dirtyTimingKeys = new Set();
  let profileDraftDirty = false;
  let profileAwaitingClean = false;
  let profileLoadPending = false;
  let applyPending = false;
  let applyRequestId = "";

  function clearTimingDirty() {
    dirtyTimingKeys.clear();
  }

  return {
    get applyPending() { return applyPending; },
    get timingDraftDirty() { return dirtyTimingKeys.size > 0; },
    get profileDraftDirty() { return profileDraftDirty; },
    get profileLoadPending() { return profileLoadPending; },
    get dirtyTimingKeys() { return new Set(dirtyTimingKeys); },

    markProfileEdit() {
      revision.markEdit();
      profileDraftDirty = true;
      profileAwaitingClean = false;
    },

    markTimingEdit(key) {
      revision.markTimingEdit();
      dirtyTimingKeys.add(key || "*");
    },

    // Call before posting save / save-as / load / select / delete.
    beginProfileMutation(loadsSettings) {
      profileAwaitingClean = true;
      if (loadsSettings) profileLoadPending = true;
    },

    // Call once per status message from the host.
    observeHostStatus(incoming) {
      if (profileAwaitingClean && incoming && incoming.profileDirty === false) {
        profileDraftDirty = false;
        profileAwaitingClean = false;
      }
    },

    // Folds the page's local flags into the last host status. No side effects.
    effectiveStatus(incoming) {
      const status = incoming || {};
      return Object.assign({}, status, {
        profileDirty: profileDraftDirty || status.profileDirty === true,
        timingsDirty: dirtyTimingKeys.size > 0 || status.timingsDirty === true,
      });
    },

    // Decides how a host `settings` message applies. Returns the keys whose
    // local (unapplied) values must survive hydration, or null to skip it.
    onSettings(settings) {
      if (applyPending) {
        revision.acknowledge(settings);
        return null;
      }
      if (profileLoadPending) {
        profileLoadPending = false;
        clearTimingDirty();
        return new Set();
      }
      return new Set(dirtyTimingKeys);
    },

    beginApply() {
      applyPending = true;
      applyRequestId = nextId();
      revision.begin(applyRequestId);
      return applyRequestId;
    },

    // Returns null when the result is not for the pending request.
    completeApply(message) {
      if (!applyPending || (applyRequestId && message.requestId !== applyRequestId)) return null;
      const result = revision.complete(message.requestId, message.success === true);
      if (result.ignored) return null;
      applyPending = false;
      applyRequestId = "";
      if (result.shouldClearTimingDirty) clearTimingDirty();
      return {
        success: result.success,
        hydrateWith: result.shouldHydrate ? result.acknowledgedSettings : null,
        newerTimingEditsPending: result.success && result.hasNewerTimingEdits,
      };
    },
  };
}
