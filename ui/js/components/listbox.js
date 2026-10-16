// A single-select listbox popup used for the hero and profile pickers.
// Markup: [root] > button[trigger][aria-haspopup=listbox] + [role=listbox][hidden]
// Keyboard: arrows, Home/End, Enter/Space choose, Escape closes, letters type ahead.
import { h, setText } from "../dom.js";

let uid = 0;

export function createListbox({ root, trigger, menu, valueLabel, onChoose }) {
  const idPrefix = "lb" + (++uid) + "-";
  let options = [];
  let selected = "";
  let highlighted = 0;
  let typeahead = "";
  let typeaheadTimer = 0;
  let signature = "";

  function isOpen() {
    return !menu.hidden;
  }

  function highlight(index, scroll) {
    if (!options.length) return;
    highlighted = Math.max(0, Math.min(index, options.length - 1));
    options.forEach((option, i) => option.element.classList.toggle("highlighted", i === highlighted));
    const active = options[highlighted].element;
    menu.setAttribute("aria-activedescendant", active.id);
    if (scroll) active.scrollIntoView({ block: "nearest" });
  }

  function place() {
    const rect = trigger.getBoundingClientRect();
    const below = window.innerHeight - rect.bottom;
    root.classList.toggle("open-up", below < 300 && rect.top > below);
  }

  function setOpen(open, focusMenu) {
    menu.hidden = !open;
    root.classList.toggle("is-open", open);
    trigger.setAttribute("aria-expanded", open ? "true" : "false");
    if (open) {
      place();
      const index = options.findIndex((option) => option.value === selected);
      highlight(index >= 0 ? index : 0, true);
      if (focusMenu) menu.focus();
    } else {
      typeahead = "";
    }
  }

  function choose(value) {
    setOpen(false);
    trigger.focus();
    onChoose(value);
  }

  function render(groups) {
    menu.replaceChildren();
    options = [];
    groups.forEach((group) => {
      const container = group.label
        ? h("div", { class: "listbox-group", role: "group", "aria-label": group.label },
          h("div", { class: "listbox-group-label", "aria-hidden": "true" }, group.label))
        : menu;
      group.options.forEach((option) => {
        const index = options.length;
        const element = h("button", {
          type: "button",
          class: "listbox-option",
          role: "option",
          id: idPrefix + index,
          tabindex: "-1",
          onpointerenter: () => highlight(index, false),
          onclick: (event) => {
            event.preventDefault();
            event.stopPropagation();
            choose(option.value);
          },
        }, option.label);
        options.push({ value: option.value, label: option.label, element });
        container.append(element);
      });
      if (container !== menu) menu.append(container);
    });
  }

  function syncSelected() {
    const match = options.find((option) => option.value.toLowerCase() === selected.toLowerCase());
    options.forEach((option) => option.element.setAttribute("aria-selected", option === match ? "true" : "false"));
    setText(valueLabel, match ? match.label : selected || "None");
  }

  trigger.addEventListener("click", () => setOpen(!isOpen(), false));
  trigger.addEventListener("keydown", (event) => {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      setOpen(true, true);
    } else if ((event.key === "Enter" || event.key === " ") && !isOpen()) {
      event.preventDefault();
      setOpen(true, true);
    } else if (event.key === "Escape" && isOpen()) {
      event.preventDefault();
      setOpen(false);
    }
  });
  menu.addEventListener("keydown", (event) => {
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      highlight(highlighted + (event.key === "ArrowDown" ? 1 : -1), true);
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      highlight(event.key === "Home" ? 0 : options.length - 1, true);
    } else if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      const option = options[highlighted];
      if (option) choose(option.value);
    } else if (event.key === "Escape" || event.key === "Tab") {
      if (event.key === "Escape") event.preventDefault();
      setOpen(false);
      if (event.key === "Escape") trigger.focus();
    } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
      typeahead += event.key.toLowerCase();
      window.clearTimeout(typeaheadTimer);
      typeaheadTimer = window.setTimeout(() => { typeahead = ""; }, 700);
      const match = options.findIndex((option) => option.label.toLowerCase().startsWith(typeahead));
      if (match >= 0) highlight(match, true);
    }
  });
  document.addEventListener("pointerdown", (event) => {
    if (isOpen() && !root.contains(event.target)) setOpen(false);
  });
  window.addEventListener("resize", () => { if (isOpen()) place(); });

  return {
    // groups: [{ label?, options: [{ value, label }] }]
    update(groups, selectedValue) {
      const next = JSON.stringify(groups);
      if (next !== signature) {
        signature = next;
        render(groups);
      }
      selected = String(selectedValue ?? "");
      syncSelected();
    },
    close() {
      setOpen(false);
    },
  };
}
