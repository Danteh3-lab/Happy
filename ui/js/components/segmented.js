// Dodge-direction radiogroup with a roving tab stop (one tabbable option).
import { ORDER, step, keyToDelta, sync } from "../model/dodge-direction.js";
import { $$ } from "../dom.js";

export function createDodgeSegmented(group, { getDirection, setDirection }) {
  const buttons = () => $$("[data-dodge]", group);

  function select(direction) {
    if (direction !== getDirection()) setDirection(direction);
    const target = buttons().find((button) => button.dataset.dodge === direction);
    if (target) target.focus();
  }

  buttons().forEach((button) => {
    button.addEventListener("click", () => select(button.dataset.dodge));
    button.addEventListener("keydown", (event) => {
      let target = null;
      const delta = keyToDelta(event.key);
      if (delta !== 0) target = step(getDirection(), delta);
      else if (event.key === "Home") target = ORDER[0];
      else if (event.key === "End") target = ORDER[ORDER.length - 1];
      else return;
      event.preventDefault();
      select(target);
    });
  });

  return {
    update(direction) {
      sync(buttons(), direction);
    },
  };
}
