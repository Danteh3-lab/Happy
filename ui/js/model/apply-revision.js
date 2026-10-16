// Tracks edits made while an Apply/Start request is in flight, so an older
// acknowledgement never overwrites newer edits or clears their pending state.

export function create() {
  let editRevision = 0;
  let timingRevision = 0;
  let pending = null;
  let acknowledgedSettings = null;

  return {
    markEdit() {
      editRevision += 1;
    },
    markTimingEdit() {
      timingRevision += 1;
    },
    begin(requestId) {
      pending = { requestId, editRevision, timingRevision };
      acknowledgedSettings = null;
    },
    acknowledge(settings) {
      if (pending) acknowledgedSettings = settings;
    },
    complete(requestId, success) {
      if (!pending || pending.requestId !== requestId) return { ignored: true };

      const result = {
        ignored: false,
        success: success === true,
        hasNewerEdits: editRevision !== pending.editRevision,
        hasNewerTimingEdits: timingRevision !== pending.timingRevision,
        acknowledgedSettings,
      };
      result.shouldHydrate = result.success && !result.hasNewerEdits && result.acknowledgedSettings != null;
      result.shouldClearTimingDirty = result.success && !result.hasNewerTimingEdits;
      pending = null;
      acknowledgedSettings = null;
      return result;
    },
  };
}
