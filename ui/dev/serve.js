// Browser preview of the control deck without WebView2 (not shipped).
//   node ui/dev/serve.js [port]   then open http://localhost:5174/?fake
// The ?fake query loads dev/fake-host.js, which plays the bot's side.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const port = Number(process.argv[2] || 5174);
const types = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
  ".json": "application/json",
};

createServer(async (request, response) => {
  const url = new URL(request.url, "http://localhost");
  const pathname = decodeURIComponent(url.pathname === "/" ? "/index.html" : url.pathname);
  const file = join(root, normalize(pathname));
  if (!file.startsWith(root)) {
    response.writeHead(403).end();
    return;
  }
  try {
    let body = await readFile(file);
    if (pathname === "/index.html" && url.searchParams.has("fake")) {
      body = body.toString().replace('<script type="module" src="js/main.js"></script>',
        '<script type="module" src="dev/fake-host.js"></script>\n  <script type="module" src="js/main.js"></script>');
    }
    response.writeHead(200, { "Content-Type": types[extname(file)] || "application/octet-stream", "Cache-Control": "no-store" });
    response.end(body);
  } catch {
    response.writeHead(404).end("Not found");
  }
}).listen(port, () => console.log("Control deck preview: http://localhost:" + port + "/?fake"));
