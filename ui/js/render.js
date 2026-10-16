// Copies view-model values into the static markup. Bindings are collected once
// at boot; each update writes only values that changed, so the 5 Hz status
// stream does not churn the DOM or re-announce live regions.
//
//   data-bind="key"           textContent
//   data-bind-state="key"     data-state attribute (ok | warn | bad | busy | idle | …)
//   data-bind-on="key"        aria-pressed + .is-on for toggle buttons
//   data-bind-disabled="key"  disabled
//   data-bind-show="key"      hidden unless truthy
//   data-bind-hide="key"      hidden when truthy
import { $$, setText, setAttr } from "./dom.js";

const KINDS = ["bind", "bindState", "bindOn", "bindDisabled", "bindShow", "bindHide"];
const SELECTOR = "[data-bind],[data-bind-state],[data-bind-on],[data-bind-disabled],[data-bind-show],[data-bind-hide]";

export function createRenderer(root) {
  const bindings = [];
  $$(SELECTOR, root).forEach((element) => {
    KINDS.forEach((kind) => {
      const key = element.dataset[kind];
      if (key) bindings.push({ element, kind, key });
    });
  });

  return {
    keys() {
      return new Set(bindings.map((binding) => binding.key));
    },
    apply(vm) {
      for (const { element, kind, key } of bindings) {
        if (!(key in vm)) continue;
        const value = vm[key];
        switch (kind) {
          case "bind":
            setText(element, value);
            break;
          case "bindState":
            setAttr(element, "data-state", value);
            break;
          case "bindOn":
            setAttr(element, "aria-pressed", value ? "true" : "false");
            element.classList.toggle("is-on", Boolean(value));
            break;
          case "bindDisabled":
            if (element.disabled !== Boolean(value)) element.disabled = Boolean(value);
            break;
          case "bindShow":
            if (element.hidden === Boolean(value)) element.hidden = !value;
            break;
          case "bindHide":
            if (element.hidden !== Boolean(value)) element.hidden = Boolean(value);
            break;
        }
      }
    },
  };
}
