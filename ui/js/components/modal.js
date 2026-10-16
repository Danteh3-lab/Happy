// One modal for messages, confirmations and single-field input, with a focus
// trap and focus restore.
import { $, $$ } from "../dom.js";

export function createModal(root) {
  const title = $("#modal-title", root);
  const eyebrow = $("#modal-eyebrow", root);
  const body = $("#modal-body", root);
  const form = $("#modal-form", root);
  const input = $("#modal-input", root);
  const inputLabel = $("#modal-input-label", root);
  const actions = $("#modal-actions", root);
  const confirmButton = $("#modal-confirm-button", root);
  const cancelButton = $("#modal-cancel-button", root);
  const closeButton = $("#modal-close-button", root);
  let callback = null;
  let returnFocus = null;

  function open(options) {
    returnFocus = document.activeElement;
    callback = options.onConfirm || null;
    eyebrow.textContent = options.eyebrow || "MESSAGE";
    title.textContent = options.title || "Message";
    body.textContent = options.body || "";
    body.hidden = !options.body;
    body.classList.toggle("is-pre", Boolean(options.pre));
    form.hidden = !options.input;
    actions.hidden = !options.onConfirm;
    confirmButton.textContent = options.confirmLabel || "Confirm";
    confirmButton.classList.toggle("btn-danger", Boolean(options.danger));
    confirmButton.classList.toggle("btn-primary", !options.danger);
    if (options.input) {
      inputLabel.textContent = options.input.label || "Value";
      input.value = options.input.value || "";
    }
    root.hidden = false;
    if (options.input) {
      input.focus();
      input.select();
    } else if (options.onConfirm) {
      cancelButton.focus();
    } else {
      closeButton.focus();
    }
  }

  function close() {
    root.hidden = true;
    callback = null;
    const target = returnFocus;
    returnFocus = null;
    if (target && target.isConnected && typeof target.focus === "function") target.focus();
  }

  function confirm() {
    const done = callback;
    const value = form.hidden ? null : input.value.trim();
    close();
    if (done) done(value);
  }

  $$("[data-modal-close]", root).forEach((element) => element.addEventListener("click", close));
  cancelButton.addEventListener("click", close);
  confirmButton.addEventListener("click", confirm);
  input.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      confirm();
    }
  });
  root.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      event.preventDefault();
      close();
      return;
    }
    if (event.key !== "Tab") return;
    const focusable = $$("button:not([disabled]), input:not([disabled])", root).filter((element) => !element.closest("[hidden]"));
    if (!focusable.length) return;
    const index = focusable.indexOf(document.activeElement);
    const next = (index + (event.shiftKey ? -1 : 1) + focusable.length) % focusable.length;
    event.preventDefault();
    focusable[next].focus();
  });

  return {
    message(titleText, bodyText, eyebrowText) {
      open({ title: titleText, body: bodyText, eyebrow: eyebrowText, pre: true });
    },
    confirm(titleText, bodyText, confirmLabel, onConfirm, options) {
      open(Object.assign({ title: titleText, body: bodyText, eyebrow: "CONFIRM", confirmLabel, onConfirm }, options));
    },
    prompt(titleText, bodyText, label, value, confirmLabel, onConfirm) {
      open({ title: titleText, body: bodyText, eyebrow: "PROFILE", confirmLabel, onConfirm, input: { label, value } });
    },
    get isOpen() {
      return !root.hidden;
    },
  };
}
