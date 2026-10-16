// Pure settings rules shared by the pages. No DOM access.
import { heroKeys } from "./hero-catalog.js";
import { normalize as normalizeDodge } from "./dodge-direction.js";

export function selectedHero(settings) {
  return heroKeys.find((hero) => settings[hero] === true) || "";
}

// Selecting a hero clears the others; choosing none also turns hero response off.
export function heroPatch(hero) {
  const patch = {};
  heroKeys.forEach((key) => { patch[key] = key === hero; });
  patch.Nohero = !hero;
  if (!hero) patch.YourHero = false;
  return patch;
}

export function heroResponseEnabled(settings) {
  return selectedHero(settings) !== "" && settings.YourHero === true && settings.Nohero !== true;
}

// Which dependent controls are usable. Mirrors the eligibility rules in
// ReactionPolicy so the page never offers a control the executor ignores.
export function controlAvailability(settings) {
  const s = settings || {};
  const legitParry = s.Autoblock === true && s.Legit === true && s.Parry === true;
  const blackPrior = s.YourHero === true && s.Nohero !== true && s.Blackprior === true;
  return {
    LegitParryChance: s.Legit === true,
    BulwarkFallback: legitParry && blackPrior,
    CrushingFallbackChance: legitParry && s.Crushing === true && s.BulwarkFallback === true && blackPrior,
    DeflectFallbackChance: legitParry && s.Deflect === true,
    OrangeLight: s.Unblockables === true,
    YourHero: selectedHero(s) !== "",
  };
}

// Keys whose availability depends on other switches.
export const AVAILABILITY_INPUTS = new Set([
  "Autoblock", "Legit", "Parry", "Crushing", "Deflect", "BulwarkFallback", "YourHero", "Nohero", "Unblockables",
  ...heroKeys,
]);

export function validationMessage(value, min, max) {
  const text = String(value ?? "").trim();
  if (!text) return "Enter a value.";
  if (!/^[-+]?\d+$/.test(text)) return "Use a whole number.";
  const number = Number(text);
  if (!Number.isFinite(number)) return "Use a valid number.";
  if (Number.isFinite(min) && number < min) return "Must be at least " + min + ".";
  if (Number.isFinite(max) && number > max) return "Must be at most " + max + ".";
  return "";
}

// Width must be at least height; the host rejects portrait calibrations.
export function resolutionMessage(width, height) {
  if (validationMessage(width, 1, 16384) || validationMessage(height, 1, 16384)) return "";
  return Number(width) < Number(height) ? "Width must be at least height." : "";
}

export function resolutionReady(settings) {
  const width = Number(settings.res1);
  const height = Number(settings.res2);
  return Number.isInteger(width) && Number.isInteger(height) && width > 0 && height > 0 && width >= height;
}

export function normalizeSettings(settings) {
  return normalizeDodge(Object.assign({}, settings || {}));
}

// Reads a ReactionBehaviorSummary member; the host sends PascalCase names.
export function behaviorValue(behavior, key, fallback) {
  if (!behavior) return fallback;
  return behavior[key] ?? behavior[key.charAt(0).toUpperCase() + key.slice(1)] ?? fallback;
}
