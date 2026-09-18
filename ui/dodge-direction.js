(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else root.HappyDodgeDirection = api;
})(typeof window !== "undefined" ? window : globalThis, function () {
  const ORDER = ["back", "left", "right"];

  function fromSettings(settings) {
    const current = settings || {};
    if (current.Rightdodge === true) return "right";
    if (current.Leftdodge === true) return "left";
    return "back";
  }

  function toSettings(direction) {
    if (direction === "left") return { Leftdodge: true, Rightdodge: false };
    if (direction === "right") return { Leftdodge: false, Rightdodge: true };
    return { Leftdodge: false, Rightdodge: false };
  }

  function normalize(settings) {
    const current = settings || {};
    if (current.Leftdodge === true && current.Rightdodge === true) {
      return Object.assign({}, current, { Leftdodge: false, Rightdodge: true });
    }
    return current;
  }

  function sync(buttons, direction) {
    Array.from(buttons || []).forEach((button) => {
      const selected = Boolean(button.dataset) && button.dataset.dodge === direction;
      if (button.classList && typeof button.classList.toggle === "function") {
        button.classList.toggle("active", selected);
      }
      if (typeof button.setAttribute === "function") {
        button.setAttribute("aria-checked", selected ? "true" : "false");
      }
      button.tabIndex = selected ? 0 : -1;
    });
  }

  function step(current, delta) {
    const index = ORDER.indexOf(current);
    const safe = index < 0 ? 0 : index;
    return ORDER[(safe + delta + ORDER.length) % ORDER.length];
  }

  function keyToDelta(key) {
    if (key === "ArrowRight" || key === "ArrowDown") return 1;
    if (key === "ArrowLeft" || key === "ArrowUp") return -1;
    return 0;
  }

  return { ORDER, fromSettings, toSettings, normalize, sync, step, keyToDelta };
});
