import test from "node:test";
import assert from "node:assert/strict";
import {
  controlAvailability, heroPatch, resolutionMessage, resolutionReady, selectedHero, validationMessage, behaviorValue,
} from "../js/model/settings-model.js";
import { heroKeys } from "../js/model/hero-catalog.js";

const base = { Autoblock: true, Legit: true, Parry: true, Crushing: true, Deflect: true, BulwarkFallback: true,
  YourHero: true, Nohero: false, Blackprior: true, Unblockables: true };

test("availability follows the reaction policy", () => {
  assert.deepEqual(controlAvailability(base), {
    LegitParryChance: true, BulwarkFallback: true, CrushingFallbackChance: true, DeflectFallbackChance: true,
    OrangeLight: true, YourHero: true,
  });
  assert.equal(controlAvailability({ ...base, Legit: false }).LegitParryChance, false);
  assert.equal(controlAvailability({ ...base, Autoblock: false }).BulwarkFallback, false);
  assert.equal(controlAvailability({ ...base, Blackprior: false }).BulwarkFallback, false);
  assert.equal(controlAvailability({ ...base, Nohero: true }).CrushingFallbackChance, false);
  assert.equal(controlAvailability({ ...base, BulwarkFallback: false }).CrushingFallbackChance, false);
  assert.equal(controlAvailability({ ...base, Deflect: false }).DeflectFallbackChance, false);
  assert.equal(controlAvailability({ ...base, Unblockables: false }).OrangeLight, false);
  assert.equal(controlAvailability({ ...base, Blackprior: false }).YourHero, false);
});

test("choosing a hero selects only that hero; none turns hero response off", () => {
  const patch = heroPatch("Orochi");
  assert.equal(Object.keys(patch).filter((key) => heroKeys.includes(key) && patch[key]).length, 1);
  assert.equal(selectedHero(patch), "Orochi");
  assert.equal(patch.Nohero, false);
  const none = heroPatch("");
  assert.equal(selectedHero(none), "");
  assert.equal(none.Nohero, true);
  assert.equal(none.YourHero, false);
});

test("number validation messages", () => {
  assert.equal(validationMessage("", 0, 100), "Enter a value.");
  assert.equal(validationMessage("1.5", 0, 100), "Use a whole number.");
  assert.equal(validationMessage("-1", 0, 100), "Must be at least 0.");
  assert.equal(validationMessage("101", 0, 100), "Must be at most 100.");
  assert.equal(validationMessage("55", 0, 100), "");
});

test("resolution must be landscape and positive", () => {
  assert.equal(resolutionMessage("1080", "1920"), "Width must be at least height.");
  assert.equal(resolutionMessage("1920", "1080"), "");
  assert.equal(resolutionReady({ res1: "1920", res2: "1080" }), true);
  assert.equal(resolutionReady({ res1: "0", res2: "1080" }), false);
});

test("behavior values accept the host's PascalCase names", () => {
  assert.equal(behaviorValue({ FAction: "Parry" }, "fAction", "None"), "Parry");
  assert.equal(behaviorValue({ fAction: "Deflect" }, "fAction", "None"), "Deflect");
  assert.equal(behaviorValue(null, "fAction", "None"), "None");
});
