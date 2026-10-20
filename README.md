# DANBOT website

The public DANBOT website: static HTML, CSS and JS served by a Cloudflare
Worker with static assets (`wrangler.jsonc`, no Worker script). Response
headers, including the CSP, live in `public/_headers`.

```powershell
npm run dev      # local preview: http://localhost:8787
npm run deploy   # publish to Cloudflare (run `npx wrangler login` once first)
```

Live at https://danbot-site.toikromodanick.workers.dev.

`public/styles/tokens.css` is a verbatim copy of `ui/styles/tokens.css` from
the bot branches (`danbotV2`); when the deck's tokens change, copy the file over.
