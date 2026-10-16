// Transient notifications. Each kind has its own icon so color is never the only cue.
import { h, icon } from "../dom.js";

const ICONS = { success: "check", error: "alert", warning: "warn", info: "info" };

export function createToaster(stack) {
  return function showToast(message, kind) {
    const type = ICONS[kind] ? kind : "info";
    const toast = h("div", { class: "toast", "data-kind": type }, icon(ICONS[type]), h("span", {}, message));
    stack.append(toast);
    while (stack.children.length > 4) stack.firstElementChild.remove();
    window.setTimeout(() => {
      toast.classList.add("is-leaving");
      window.setTimeout(() => toast.remove(), 220);
    }, type === "error" ? 4600 : 2800);
  };
}
