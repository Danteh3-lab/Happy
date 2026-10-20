// DANBOT website: header state, mobile menu, current-section dot, the Active
// Behavior demo, counters and the HUD animations. Everything is progressive:
// without this file the page still reads top to bottom.

const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const canObserve = "IntersectionObserver" in window;

/* ---------------------------------------------------------------- header */
// Over the top of the hero the header is a plain full-width bar; once the page
// scrolls it settles into the floating bar (the default without this script).
function initHeader() {
  const header = document.querySelector(".site-header");
  if (!header) return;
  // Writes only when the state flips, so scrolling doesn't restyle the header every frame.
  const update = () => {
    const top = String(window.scrollY < 16);
    if (header.dataset.top !== top) header.dataset.top = top;
  };
  update();
  requestAnimationFrame(() => { header.dataset.ready = ""; });
  window.addEventListener("scroll", update, { passive: true });
}

/* ---------------------------------------------------------------- menu */
function initMenu() {
  const header = document.querySelector(".site-header");
  const button = header && header.querySelector(".menu-btn");
  const nav = document.getElementById("site-nav");
  if (!button || !nav) return;

  const isOpen = () => button.getAttribute("aria-expanded") === "true";
  const setOpen = (open) => {
    header.dataset.menu = open ? "open" : "closed";
    button.setAttribute("aria-expanded", String(open));
    button.setAttribute("aria-label", open ? "Close menu" : "Open menu");
  };

  button.addEventListener("click", () => setOpen(!isOpen()));
  nav.addEventListener("click", (event) => {
    if (event.target.closest("a")) setOpen(false);
  });
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape" || !isOpen()) return;
    setOpen(false);
    button.focus();
  });
  window.matchMedia("(min-width: 901px)").addEventListener("change", (event) => {
    if (event.matches) setOpen(false);
  });
}

/* ---------------------------------------------------------------- current section */
function initSectionDot() {
  const links = new Map();
  for (const link of document.querySelectorAll('.site-nav a[href^="#"]')) {
    const section = document.getElementById(link.hash.slice(1));
    if (section) links.set(section, link);
  }
  if (!links.size || !canObserve) return;

  // A section is current while it crosses a thin band in the middle of the
  // viewport; the first one in document order wins.
  const crossing = new Set();
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (entry.isIntersecting) crossing.add(entry.target);
      else crossing.delete(entry.target);
    }
    const current = [...links.keys()].find((section) => crossing.has(section));
    for (const [section, link] of links) {
      if (section === current) link.setAttribute("aria-current", "true");
      else link.removeAttribute("aria-current");
    }
  }, { rootMargin: "-45% 0px -50% 0px" });
  for (const section of links.keys()) observer.observe(section);
}

/* ---------------------------------------------------------------- active behavior demo */
const DEMO_TIMING = { parryDelay: 60, legitChance: 55, deflectChance: 50, left: 90, top: 110, right: 90 };

// Mirrors Combat/ReactionBehaviorSummary.Create for these four switches,
// with no hero selected, Crushing off and the default timings.
function describeLt(s, t = DEMO_TIMING) {
  const configured = s.parry ? (s.legit ? "Mixed parry" : "Parry") : s.deflect ? "Deflect" : "None";
  const notices = [];
  let action;
  let detail;
  if (!s.autoblock) {
    action = "Inactive";
    detail = configured === "None"
      ? "No LT reaction is configured."
      : configured + " is configured, but Auto Block is required.";
    if (configured !== "None") notices.push("Enable Auto Block to arm LT reactions.");
  } else if (s.parry) {
    action = s.legit ? "Mixed parry" : "Parry";
    const deflectFirst = s.deflect
      ? `${t.deflectChance}% deflect first (${t.left}/${t.top}/${t.right} ms L/T/R), `
      : "";
    detail = s.legit
      ? `${t.legitChance}% parry after ${t.parryDelay} ms; failed rolls: ${deflectFirst}otherwise block only.`
      : `Parry after the flash gate and ${t.parryDelay} ms delay.`;
  } else if (s.deflect) {
    action = "Deflect";
    detail = `Directional dodge after the flash gate: ${t.left} ms left, ${t.top} ms top, ${t.right} ms right.`;
  } else {
    action = "None";
    detail = "Holding LT will only keep the normal guard active.";
  }
  if (s.parry && !s.legit && s.deflect) {
    notices.push("Deflect never runs: Parry takes priority. Enable Legit to use it as a fallback.");
  }
  if (s.legit && !s.parry) notices.push("Legit has no effect: it only rolls for Parry.");
  return { action, detail, notices, state: action === "None" || action === "Inactive" ? "idle" : "ok" };
}

function initBehaviorDemo() {
  const demo = document.querySelector("[data-behavior-demo]");
  if (!demo) return;
  const inputs = demo.querySelectorAll("input[data-switch]");
  const row = demo.querySelector(".action-row");
  const action = demo.querySelector("[data-lt-action]");
  const detail = demo.querySelector("[data-lt-detail]");
  const notices = demo.querySelector("[data-lt-notices]");

  const render = () => {
    const switches = {};
    for (const input of inputs) switches[input.dataset.switch] = input.checked;
    const view = describeLt(switches);
    row.dataset.state = view.state;
    action.textContent = view.action;
    detail.textContent = view.detail;
    notices.replaceChildren(...view.notices.map((text) => {
      const item = document.createElement("li");
      item.className = "notice";
      item.textContent = text;
      return item;
    }));
    notices.hidden = view.notices.length === 0;
  };
  demo.addEventListener("change", render);
  render();
}

/* ---------------------------------------------------------------- HUD stages */
const STAGES = [
  { stage: "guard", text: "LEFT → GUARD" },
  { stage: "ready", text: "LEFT → PARRY READY" },
  { stage: "sent", text: "LEFT → PARRY SENT" },
];
const STAGE_MS = 1400;

function initStageCycles() {
  const hosts = document.querySelectorAll("[data-stage-cycle]");
  if (!hosts.length || reduceMotion.matches || !canObserve) return; // markup already shows the last stage

  const show = (host, stage) => {
    host.dataset.stage = stage.stage;
    for (const text of host.querySelectorAll("[data-stage-text]")) text.textContent = stage.text;
  };
  // Plays the sequence once each time the HUD scrolls into view, so it never
  // keeps changing for more than a few seconds unasked.
  const play = (host) => {
    if (host.dataset.playing === "true") return;
    host.dataset.playing = "true";
    STAGES.forEach((stage, i) => setTimeout(() => show(host, stage), i * STAGE_MS));
    setTimeout(() => { host.dataset.playing = "false"; }, STAGES.length * STAGE_MS);
  };
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) if (entry.isIntersecting) play(entry.target);
  }, { threshold: 0.6 });
  for (const host of hosts) observer.observe(host);
}

/* ---------------------------------------------------------------- counters */
// Counts each stat up from zero the first time it scrolls into view. The
// markup already holds the final value.
function initCounters() {
  const counters = document.querySelectorAll("[data-count]");
  if (!counters.length || reduceMotion.matches || !canObserve) return;

  const run = (el) => {
    const target = Number(el.dataset.count);
    if (!target) return;
    const start = performance.now();
    const tick = (now) => {
      const t = Math.min(1, (now - start) / 1200);
      el.textContent = String(Math.round(target * (1 - Math.pow(1 - t, 3))));
      if (t < 1) requestAnimationFrame(tick);
    };
    el.textContent = "0";
    requestAnimationFrame(tick);
  };
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      observer.unobserve(entry.target);
      run(entry.target);
    }
  }, { threshold: 0.6 });
  for (const el of counters) observer.observe(el);
}

/* ---------------------------------------------------------------- aurora */
// Pauses the background drift while its band is off screen, so the hero and
// closing band don't keep compositing frames nobody sees.
function initAurora() {
  const bands = document.querySelectorAll(".art");
  if (!bands.length || !canObserve) return;
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) entry.target.toggleAttribute("data-offscreen", !entry.isIntersecting);
  });
  for (const band of bands) observer.observe(band);
}

/* ---------------------------------------------------------------- hero dots */
// Redraws the hero's dot grid on a canvas so dots near the cursor swell, warm
// to gold and lean away from it. Touch screens and reduced motion keep the
// static CSS grid. Frames only run while dots are still moving.
const DOT_GAP = 32;
const DOT_RADIUS = 1.2;
const DOT_REACH = 150;
const DOT_PUSH = 9;
const DOT_GOLD = [251, 191, 36];

function initDotField() {
  const art = document.querySelector(".hero .art");
  const finePointer = window.matchMedia("(hover: hover) and (pointer: fine)");
  if (!art || reduceMotion.matches || !finePointer.matches) return;
  const canvas = document.createElement("canvas");
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  canvas.className = "dot-field";
  art.append(canvas);
  art.dataset.dots = "canvas";

  const pointer = { x: -1e4, y: -1e4, strength: 0, target: 0 };
  let width = 0;
  let height = 0;
  let dots = [];
  let frame = 0;

  const draw = () => {
    frame = 0;
    pointer.strength += (pointer.target - pointer.strength) * 0.12;
    let moving = Math.abs(pointer.target - pointer.strength) > 0.005;
    ctx.clearRect(0, 0, width, height);
    for (const dot of dots) {
      const dx = dot.x - pointer.x;
      const dy = dot.y - pointer.y;
      const dist = Math.hypot(dx, dy) || 1;
      const k = dist < DOT_REACH ? (1 - dist / DOT_REACH) ** 2 * pointer.strength : 0;
      dot.ox += ((dx / dist) * k * DOT_PUSH - dot.ox) * 0.18;
      dot.oy += ((dy / dist) * k * DOT_PUSH - dot.oy) * 0.18;
      if (Math.abs(dot.ox) > 0.05 || Math.abs(dot.oy) > 0.05) moving = true;
      const [r, g, b] = DOT_GOLD.map((c) => Math.round(255 + (c - 255) * k));
      ctx.fillStyle = `rgba(${r}, ${g}, ${b}, ${0.3 + k * 0.6})`;
      ctx.beginPath();
      ctx.arc(dot.x + dot.ox, dot.y + dot.oy, DOT_RADIUS + k * 1.8, 0, Math.PI * 2);
      ctx.fill();
    }
    if (moving) request();
  };
  const request = () => { if (!frame) frame = requestAnimationFrame(draw); };

  // Same layout as the CSS grid: 32px tiles centred on the band.
  const resize = () => {
    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    width = art.clientWidth;
    height = art.clientHeight;
    canvas.width = Math.round(width * ratio);
    canvas.height = Math.round(height * ratio);
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    dots = [];
    for (let y = (height / 2) % DOT_GAP; y < height; y += DOT_GAP) {
      for (let x = (width / 2) % DOT_GAP; x < width; x += DOT_GAP) dots.push({ x, y, ox: 0, oy: 0 });
    }
    request();
  };

  const hero = art.parentElement;
  hero.addEventListener("pointermove", (event) => {
    const rect = art.getBoundingClientRect();
    pointer.x = event.clientX - rect.left;
    pointer.y = event.clientY - rect.top;
    pointer.target = 1;
    request();
  });
  hero.addEventListener("pointerleave", () => { pointer.target = 0; request(); });
  if ("ResizeObserver" in window) new ResizeObserver(resize).observe(art);
  else window.addEventListener("resize", resize);
  resize();
}

initHeader();
initMenu();
initSectionDot();
initBehaviorDemo();
initStageCycles();
initCounters();
initAurora();
initDotField();
