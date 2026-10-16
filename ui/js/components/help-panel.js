// Slide-over help panel (setup guide, hotkeys, mapping, feature notes).
import { $$ } from "../dom.js";

export function createHelpPanel(panel) {
  let returnFocus = null;
  const toggles = () => $$("[data-action='help']");

  function setOpen(open, section) {
    if (open) {
      returnFocus = document.activeElement;
      panel.hidden = false;
      // Next frame so the slide-in transition runs.
      window.requestAnimationFrame(() => panel.classList.add("is-open"));
      const target = section ? panel.querySelector("#" + section) : null;
      (target || panel.querySelector("h2")).focus();
    } else {
      panel.classList.remove("is-open");
      panel.hidden = true;
      if (returnFocus && returnFocus.isConnected) returnFocus.focus();
      returnFocus = null;
    }
    toggles().forEach((button) => button.setAttribute("aria-expanded", open ? "true" : "false"));
  }

  panel.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      event.preventDefault();
      setOpen(false);
    }
  });
  $$("[data-help-close]", panel).forEach((button) => button.addEventListener("click", () => setOpen(false)));

  return {
    toggle(section) {
      setOpen(panel.hidden, section);
    },
    open(section) {
      setOpen(true, section);
    },
  };
}
