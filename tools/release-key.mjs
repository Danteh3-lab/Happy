// Creates the ECDSA P-256 key that signs DANBOT update manifests.
//   node tools/release-key.mjs [private-key-path]
// The private key is written outside the repo (default %USERPROFILE%\.danbot\release-key.pem)
// and must never be committed. Paste the printed public key into Update/UpdateChannel.cs.
// Losing the private key means installed copies can no longer be updated automatically.
import { generateKeyPairSync } from "node:crypto";
import { existsSync, mkdirSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

const path = process.argv[2] ?? join(homedir(), ".danbot", "release-key.pem");
if (existsSync(path)) {
  console.error(`${path} already exists; refusing to overwrite the release key.`);
  process.exit(1);
}

const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
mkdirSync(dirname(path), { recursive: true });
writeFileSync(path, privateKey.export({ type: "pkcs8", format: "pem" }), { mode: 0o600 });

console.log(`Private key: ${path} (back it up somewhere safe)`);
console.log("Public key (UpdateChannel.PublicKey):");
console.log(publicKey.export({ type: "spki", format: "der" }).toString("base64"));
