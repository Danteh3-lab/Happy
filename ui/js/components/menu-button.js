// Overflow menu: button[aria-haspopup=menu] + [role=menu] of [role=menuitem] buttons.
import { $$ } from "../dom.js";

export function createMenuButton(root, trigger, menu, onSelect) {
  const items = () => $$("[role=menuitem]:not([disabled])", menu);

  function setOpen(open, focusFirst) {
    menu.hidden = !open;
    root.classList.toggle("is-open", open);
    trigger.setAttribute("aria-expanded", open ? "true" : "false");
    if (open && focusFirst) items()[0]?.focus();
  }

  function move(delta) {
    const list = items();
    if (!list.length) return;
    const index = list.indexOf(document.activeElement);
    list[(index + delta + list.length) % list.length].focus();
  }

  trigger.addEventListener("click", () => setOpen(menu.hidden, true));
  trigger.addEventListener("keydown", (event) => {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      setOpen(true, true);
    }
  });
  menu.addEventListener("keydown", (event) => {
    if (event.key === "ArrowDown") { event.preventDefault(); move(1); }
    else if (event.key === "ArrowUp") { event.preventDefault(); move(-1); }
    else if (event.key === "Home") { event.preventDefault(); items()[0]?.focus(); }
    else if (event.key === "End") { event.preventDefault(); items().at(-1)?.focus(); }
    else if (event.key === "Escape") { event.preventDefault(); setOpen(false); trigger.focus(); }
    else if (event.key === "Tab") setOpen(false);
  });
  menu.addEventListener("click", (event) => {
    const item = event.target.closest("[role=menuitem]");
    if (!item) return;
    event.stopPropagation();
    setOpen(false);
    trigger.focus();
    onSelect(item.dataset.menuAction);
  });
  document.addEventListener("pointerdown", (event) => {
    if (!menu.hidden && !root.contains(event.target)) setOpen(false);
  });
}
