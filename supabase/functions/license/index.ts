// DANBOT license service.
//
// POST {action: "check" | "redeem", device_id, device_name, key?} with the
// user's access token returns {token, status}. The token is
// base64url(payload) + "." + base64url(ECDSA P-256 / SHA-256 signature over
// the payload part); the app verifies it with the embedded public key and
// trusts it offline until payload.until.
//
// GET returns {public_key} (SPKI, base64) so the key can be embedded in the app.
//
// Deployed with verify_jwt off: the function checks the bearer token itself
// through Supabase Auth, which also works with asymmetric JWT signing keys.
import { createClient } from "npm:@supabase/supabase-js@2.49.4";

const GRACE_SECONDS = 72 * 3600;
const ALGORITHM = { name: "ECDSA", namedCurve: "P-256" };

const serviceKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ||
  JSON.parse(Deno.env.get("SUPABASE_SECRET_KEYS") ?? "{}").default;
const admin = createClient(Deno.env.get("SUPABASE_URL")!, serviceKey, {
  auth: { persistSession: false, autoRefreshToken: false },
});

type SigningKeys = { privateKey: CryptoKey; publicKey: string };
let signingKeys: Promise<SigningKeys> | null = null;

function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary);
}

function fromBase64(text: string): Uint8Array {
  return Uint8Array.from(atob(text), (c) => c.charCodeAt(0));
}

const toBase64Url = (bytes: Uint8Array) =>
  toBase64(bytes).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

// The key pair is created here on first use and stored in Vault, so the
// private half never exists outside Supabase.
async function loadKeys(): Promise<SigningKeys> {
  let { data, error } = await admin.rpc("license_signing_key");
  if (error) throw error;
  if (!data) {
    const pair = await crypto.subtle.generateKey(ALGORITHM, true, ["sign", "verify"]) as CryptoKeyPair;
    const pkcs8 = toBase64(new Uint8Array(await crypto.subtle.exportKey("pkcs8", pair.privateKey)));
    const spki = toBase64(new Uint8Array(await crypto.subtle.exportKey("spki", pair.publicKey)));
    ({ data, error } = await admin.rpc("license_init_signing_key", { p_private: pkcs8, p_public: spki }));
    if (error) throw error;
  }
  const privateKey = await crypto.subtle.importKey("pkcs8", fromBase64(data.private), ALGORITHM, false, ["sign"]);
  return { privateKey, publicKey: data.public };
}

function keys(): Promise<SigningKeys> {
  signingKeys ??= loadKeys().catch((e) => {
    signingKeys = null;
    throw e;
  });
  return signingKeys;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

async function sign(payload: Record<string, unknown>): Promise<string> {
  const part = toBase64Url(new TextEncoder().encode(JSON.stringify(payload)));
  const { privateKey } = await keys();
  const signature = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, privateKey,
    new TextEncoder().encode(part));
  return part + "." + toBase64Url(new Uint8Array(signature));
}

Deno.serve(async (req) => {
  try {
    if (req.method === "GET") return json({ public_key: (await keys()).publicKey });
    if (req.method !== "POST") return json({ error: "Method not allowed." }, 405);

    const jwt = (req.headers.get("Authorization") ?? "").replace(/^Bearer\s+/i, "");
    const { data: auth, error: authError } = jwt ? await admin.auth.getUser(jwt) : { data: null, error: true };
    const user = auth?.user;
    if (authError || !user) return json({ error: "Sign in again to check the license." }, 401);

    const body = await req.json().catch(() => ({}));
    const deviceId = String(body.device_id ?? "").toLowerCase();
    const deviceName = String(body.device_name ?? "").slice(0, 64);
    if (!/^[0-9a-f]{64}$/.test(deviceId)) return json({ error: "Invalid device id." }, 400);

    const args = { p_user: user.id, p_device_id: deviceId, p_device_name: deviceName };
    let call;
    if (body.action === "redeem") call = admin.rpc("license_redeem", { ...args, p_key: String(body.key ?? "") });
    else if (body.action === "check") call = admin.rpc("license_check", args);
    else return json({ error: "Unknown action." }, 400);

    const { data: result, error } = await call;
    if (error) throw error;
    if (result.error) return json({ error: result.error }, 400);

    const now = Math.floor(Date.now() / 1000);
    const expires = result.expires_at ? Math.floor(Date.parse(result.expires_at) / 1000) : null;
    const token = await sign({
      v: 1,
      sub: user.id,
      dev: deviceId,
      status: result.status,
      lic: result.license_id ?? null,
      iat: now,
      until: expires === null ? now + GRACE_SECONDS : Math.min(now + GRACE_SECONDS, expires),
      expires,
      max_devices: result.max_devices ?? 0,
      devices_used: result.devices_used ?? 0,
      hint: result.key_hint ?? "",
    });
    return json({ token, status: result.status });
  } catch (e) {
    console.error(e);
    return json({ error: "The license service failed. Try again in a minute." }, 500);
  }
});
