// One-step release: bump the version, publish the single exe, sign and upload it
// (tools/release.mjs --upload), then commit the bump.
//   node tools/ship.mjs [patch|minor|major] [--dry-run]
// patch is the default. --dry-run only prints what would happen.
// Tracked files must be committed first, so a release always matches a commit.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
const dryRun = args.includes("--dry-run");
const part = args.find((arg) => !arg.startsWith("--")) ?? "patch";

const fail = (message) => { console.error(message); process.exit(1); };
const run = (command, commandArgs) => {
  const result = spawnSync(command, commandArgs, { cwd: root, stdio: "inherit" });
  return result.status === 0;
};
const git = (...gitArgs) => spawnSync("git", gitArgs, { cwd: root, encoding: "utf8" }).stdout.trim();

if (!["patch", "minor", "major"].includes(part)) fail(`Unknown bump "${part}"; use patch, minor or major.`);
if (git("status", "--porcelain", "--untracked-files=no")) fail("Commit or stash your changes first.");

const files = { buildInfo: join(root, "BuildInfo.cs"), project: join(root, "HappyBot.csproj") };
const original = { buildInfo: readFileSync(files.buildInfo, "utf8"), project: readFileSync(files.project, "utf8") };

const current = /Version\s*=\s*"(\d+)\.(\d+)\.(\d+)"/.exec(original.buildInfo);
if (!current) fail("Could not read a major.minor.patch version from BuildInfo.cs.");
let [major, minor, patch] = current.slice(1).map(Number);
if (part === "major") { major++; minor = 0; patch = 0; }
else if (part === "minor") { minor++; patch = 0; }
else patch++;
const from = current.slice(1).join(".");
const next = `${major}.${minor}.${patch}`;

console.log(`DANBOT ${from} -> ${next}`);
if (dryRun) {
  console.log("Dry run: would bump the version, publish, sign, upload and commit.");
  process.exit(0);
}

const restore = (message) => {
  writeFileSync(files.buildInfo, original.buildInfo);
  writeFileSync(files.project, original.project);
  fail(`${message} The version is back at ${from}.`);
};

writeFileSync(files.buildInfo, original.buildInfo.replace(current[0], current[0].replace(from, next)));
writeFileSync(files.project, original.project
  .replace(/<Version>[\d.]+<\/Version>/, `<Version>${next}</Version>`)
  .replace(/<AssemblyVersion>[\d.]+<\/AssemblyVersion>/, `<AssemblyVersion>${next}.0</AssemblyVersion>`)
  .replace(/<FileVersion>[\d.]+<\/FileVersion>/, `<FileVersion>${next}.0</FileVersion>`)
  .replace(/<InformationalVersion>[\d.]+<\/InformationalVersion>/, `<InformationalVersion>${next}</InformationalVersion>`));

if (!run("dotnet", ["publish", "HappyBot.csproj", "-p:PublishProfile=SingleExe"])) restore("The publish failed.");
if (!run(process.execPath, [join(root, "tools", "release.mjs"), "--upload"])) {
  restore("The release failed (latest.txt goes up last, so copies still see the old release).");
}

if (!run("git", ["commit", "-q", "-m", `Release ${next}`, "--", "BuildInfo.cs", "HappyBot.csproj"])) {
  fail(`DANBOT ${next} is live, but committing the bump failed; commit BuildInfo.cs and HappyBot.csproj yourself.`);
}
console.log(`DANBOT ${next} is live and committed. Push when ready.`);
