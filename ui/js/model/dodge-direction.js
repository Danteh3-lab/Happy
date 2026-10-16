// Orange dodge direction: one radio value stored as two exclusive booleans.

export const ORDER = ["back", "left", "right"];

export function fromSettings(settings) {
  const current = settings || {};
  if (current.Rightdodge === true) return "right";
  if (current.Leftdodge === true) return "left";
  return "back";
}

export function toSettings(direction) {
  if (direction === "left") return { Leftdodge: true, Rightdodge: false };
  if (direction === "right") return { Leftdodge: false, Rightdodge: true };
  return { Leftdodge: false, Rightdodge: false };
}

// Legacy profiles may have both flags set; Right wins, as in the host.
export function normalize(settings) {
  const current = settings || {};
  if (current.Leftdodge === true && current.Rightdodge === true) {
    return Object.assign({}, current, { Leftdodge: false, Rightdodge: true });
  }
  return current;
}

export function sync(buttons, direction) {
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

export function step(current, delta) {
  const index = ORDER.indexOf(current);
  const safe = index < 0 ? 0 : index;
  return ORDER[(safe + delta + ORDER.length) % ORDER.length];
}

export function keyToDelta(key) {
  if (key === "ArrowRight" || key === "ArrowDown") return 1;
  if (key === "ArrowLeft" || key === "ArrowUp") return -1;
  return 0;
}
