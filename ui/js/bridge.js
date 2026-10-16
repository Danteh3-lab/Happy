// The WebView2 message channel. Outside WebView2 (browser preview) a fake host
// installed by ui/dev/fake-host.js stands in; otherwise sends are dropped.

export function createBridge() {
  const host = (window.chrome && window.chrome.webview) || window.__danbotFakeHost || null;
  const listeners = [];

  if (host) {
    host.addEventListener("message", (event) => {
      const message = (event && event.data) || {};
      listeners.forEach((listener) => listener(message));
    });
  }

  return {
    available: Boolean(host),
    send(type, extra) {
      if (!host) return;
      host.postMessage(Object.assign({ type }, extra || {}));
    },
    onMessage(listener) {
      listeners.push(listener);
    },
  };
}
