// Serves DANBOT releases from the danbot-downloads R2 bucket; everything else is
// a static asset. tools/release.mjs (bot repo) uploads:
//   releases/DANBOT-<version>.exe   the exe, never overwritten
//   latest.txt                      the signed manifest the app checks at startup
//
//   /download                       -> 302 to the newest release
//   /download/latest.txt            -> the manifest, never cached
//   /download/DANBOT-<version>.exe  -> that release, saved as DANBOT.exe

const RELEASE = /^\/download\/(DANBOT-\d+(?:\.\d+){1,3}\.exe)$/;

export default {
  async fetch(request, env) {
    const { pathname } = new URL(request.url);
    if (request.method !== "GET" && request.method !== "HEAD") {
      return new Response("Method not allowed", { status: 405, headers: { Allow: "GET, HEAD" } });
    }

    if (pathname === "/download/latest.txt") {
      return serve(request, env, "latest.txt", {
        "Content-Type": "text/plain; charset=utf-8",
        "Cache-Control": "no-cache",
      });
    }

    const release = RELEASE.exec(pathname);
    if (release) {
      return serve(request, env, `releases/${release[1]}`, {
        "Content-Type": "application/vnd.microsoft.portable-executable",
        "Content-Disposition": 'attachment; filename="DANBOT.exe"',
        "Cache-Control": "public, max-age=31536000, immutable",
      });
    }

    if (pathname === "/download" || pathname === "/download/") {
      const latest = await newestReleasePath(env);
      if (!latest) return notYet();
      return new Response(null, { status: 302, headers: { Location: latest, "Cache-Control": "no-cache" } });
    }

    return env.ASSETS.fetch(request);
  },
};

async function serve(request, env, key, headers) {
  const object = request.method === "HEAD" ? await env.DOWNLOADS.head(key) : await env.DOWNLOADS.get(key);
  if (!object) return notYet();
  return new Response(request.method === "HEAD" ? null : object.body, {
    headers: {
      ...headers,
      "Content-Length": String(object.size),
      ETag: object.httpEtag,
      "X-Content-Type-Options": "nosniff",
    },
  });
}

// The manifest's payload part is base64url JSON whose url points at the release.
async function newestReleasePath(env) {
  const latest = await env.DOWNLOADS.get("latest.txt");
  if (!latest) return null;
  try {
    const part = (await latest.text()).trim().split(".")[0];
    const json = atob(part.replace(/-/g, "+").replace(/_/g, "/"));
    const path = new URL(JSON.parse(json).url).pathname;
    return RELEASE.test(path) ? path : null;
  } catch {
    return null;
  }
}

function notYet() {
  return new Response("No DANBOT release is published yet.", {
    status: 404,
    headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-cache" },
  });
}
