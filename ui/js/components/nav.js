// Page switching: sections use the hidden attribute, the active nav item gets
// aria-current="page", Ctrl+1…5 jump between pages, and the last page is
// remembered per viewer.
import { $, $$ } from "../dom.js";

const STORAGE_KEY = "danbot.page";

function readStored() {
  try { return window.localStorage.getItem(STORAGE_KEY); } catch { return null; }
}

function store(page) {
  try { window.localStorage.setItem(STORAGE_KEY, page); } catch { /* storage unavailable */ }
}

export function createNav({ onChange } = {}) {
  const pages = $$("[data-page]");
  const names = pages.map((page) => page.dataset.page);
  let current = "";

  function select(name, { focus = false } = {}) {
    if (!names.includes(name)) name = names[0];
    current = name;
    pages.forEach((page) => { page.hidden = page.dataset.page !== name; });
    $$("[data-nav-item]").forEach((item) => {
      if (item.dataset.navItem === name) item.setAttribute("aria-current", "page");
      else item.removeAttribute("aria-current");
    });
    const scroller = $("#page-scroll");
    if (scroller) scroller.scrollTop = 0;
    store(name);
    if (focus) $("[data-page='" + name + "'] h1")?.focus();
    if (onChange) onChange(name);
  }

  document.addEventListener("click", (event) => {
    const target = event.target.closest("[data-nav]");
    if (!target) return;
    event.preventDefault();
    select(target.dataset.nav, { focus: !target.hasAttribute("data-nav-item") });
    if (target.dataset.navFocus) $(target.dataset.navFocus)?.focus();
  });
  document.addEventListener("keydown", (event) => {
    if (!event.ctrlKey || event.altKey || event.metaKey) return;
    const index = Number(event.key) - 1;
    if (Number.isInteger(index) && index >= 0 && index < names.length) {
      event.preventDefault();
      select(names[index], { focus: true });
    }
  });

  select(readStored() || names[0]);

  return {
    select,
    get current() { return current; },
    pageOf(element) { return element.closest("[data-page]")?.dataset.page || ""; },
  };
}
