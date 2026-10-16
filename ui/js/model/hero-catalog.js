// Hero keys match Settings.Chars in Settings.cs (checked by ia-coverage.test.js).

export const heroGroups = [
  { label: "Knights", keys: ["Warden", "Peacekeeper", "Centurion", "Blackprior", "Gryphon", "Conqueror", "Lawbringer", "Gladiator", "Warmonger"] },
  { label: "Vikings", keys: ["Raider", "Berserker", "Highlander", "Jormungandr", "Warlord", "Valkyrie", "Shaman", "Varangian", "Null"] },
  { label: "Samurai", keys: ["Kensei", "Orochi", "Shinobi", "Hitokiri", "Sohei", "Shugoki", "Nobushi", "Aramusha", "Kyoshin"] },
  { label: "Wu Lin", keys: ["Tiandi", "Nuxia", "Zhanhu", "Jiangjun", "Shaolin", "Juren"] },
  { label: "Outlanders", keys: ["Pirate", "Afeera", "Medjay", "Khatun", "Ocelotl", "Virtuosa"] },
];

export const heroKeys = heroGroups.flatMap((group) => group.keys);

const labels = { Blackprior: "Black Prior", Jiangjun: "Jiang Jun" };

export function heroLabel(key) {
  if (!key || key === "None") return "None";
  return labels[key] || key;
}
