// Signs publish\DANBOT.exe as the newest release and, with --upload, publishes it
// for auto-update (R2 bucket danbot-downloads, served by the website's Worker).
//   dotnet publish HappyBot.csproj -p:PublishProfile=SingleExe
//   node tools/release.mjs [--upload] [--key path\to\release-key.pem]
// The version comes from BuildInfo.cs, so a release can never claim a version its
// exe does not report (which would make every copy re-download it).
import { createHash, createPrivateKey, createPublicKey, sign, verify } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const SITE = "https://danbot-site.toikromodanick.workers.dev";
const BUCKET = "danbot-downloads";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
const upload = args.includes("--upload");
const keyIndex = args.indexOf("--key");
const keyPath = keyIndex >= 0 ? args[keyIndex + 1] : join(homedir(), ".danbot", "release-key.pem");

const fail = (message) => { console.error(message); process.exit(1); };
const read = (file) => readFileSync(join(root, file), "utf8");

const version = /Version\s*=\s*"([\d.]+)"/.exec(read("BuildInfo.cs"))?.[1];
const projectVersion = /<Version>([\d.]+)<\/Version>/.exec(read("HappyBot.csproj"))?.[1];
if (!version) fail("Could not read the version from BuildInfo.cs.");
if (version !== projectVersion) fail(`BuildInfo.cs says ${version} but HappyBot.csproj says ${projectVersion}; make them match.`);

const exePath = join(root, "publish", "DANBOT.exe");
let exe;
try { exe = readFileSync(exePath); } catch { fail(`${exePath} is missing; run the SingleExe publish first.`); }

const key = createPrivateKey(readFileSync(keyPath));
const publicKey = createPublicKey(key).export({ type: "spki", format: "der" }).toString("base64");
if (!read("Update/UpdateChannel.cs").includes(publicKey)) {
  fail("This key does not match UpdateChannel.PublicKey; installed copies would reject the release.");
}

const payload = {
  v: 1,
  version,
  url: `${SITE}/download/DANBOT-${version}.exe`,
  size: exe.length,
  sha256: createHash("sha256").update(exe).digest("hex"),
};
const part = Buffer.from(JSON.stringify(payload)).toString("base64url");
const signature = sign("sha256", Buffer.from(part, "ascii"), { key, dsaEncoding: "ieee-p1363" });
if (!verify("sha256", Buffer.from(part, "ascii"), { key: createPublicKey(key), dsaEncoding: "ieee-p1363" }, signature)) {
  fail("The signature did not verify.");
}
const manifestPath = join(root, "publish", "latest.txt");
writeFileSync(manifestPath, `${part}.${signature.toString("base64url")}\n`);
console.log(`Signed DANBOT ${version} (${(exe.length / 1048576).toFixed(1)} MB, sha256 ${payload.sha256}).`);
console.log(`Manifest: ${manifestPath}`);

if (!upload) {
  console.log("Run again with --upload to publish it.");
  process.exit(0);
}

// The exe goes up first, so the manifest never points at a missing file.
// npx.cmd needs the shell, which splits unquoted arguments on spaces ("text/plain; charset=utf-8").
const put = (object, file, contentType) => {
  const result = spawnSync("npx.cmd", ["--yes", "wrangler@4.151.2", "r2", "object", "put", `${BUCKET}/${object}`,
    "--file", `"${file}"`, "--content-type", `"${contentType}"`, "--remote"], { stdio: "inherit", shell: true });
  if (result.status !== 0) fail(`Uploading ${object} failed.`);
};
put(`releases/DANBOT-${version}.exe`, exePath, "application/vnd.microsoft.portable-executable");
put("latest.txt", manifestPath, "text/plain; charset=utf-8");
console.log(`Released DANBOT ${version}. Copies update on their next start.`);
