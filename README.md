# DANBOT

DANBOT by Danteh is a C#/.NET 10 rewrite of the original `archive/legacy-ahk/HappyBot.ahk`. It provides screen-based attack detection, guard automation, parry, deflect, dodge, and hero-specific responses.

The only input path is a DualShock 4 read directly over HID and merged into
one virtual Xbox controller:

```text
DS4 (HID, hidden by HidHide) -> HappyBot state merge -> one ViGEm Xbox controller -> game
```

There is no keyboard or mouse input or output: no hotkeys, no keyboard hold
keys, and no SendInput/keybd_event modes.

This avoids the input-device switching lag caused by exposing both the source controller and the bot controller to the game.

## Requirements

- Windows 10/11 64-bit
- .NET 10 SDK
- ViGEmBus
- A DualShock 4 controller
- HidHide

## Build

The app ships as one self-contained file:

```powershell
dotnet publish HappyBot.csproj -p:PublishProfile=SingleExe
```

This writes `publish\DANBOT.exe` (about 50 MB). It bundles the .NET runtime,
the control deck UI and the WebView2 loader, so the PC needs no .NET install;
it still needs the ViGEmBus driver and the WebView2 runtime (built into
Windows 10/11). Profiles, settings, telemetry and WebView2 data live in
`%LOCALAPPDATA%\DANBOT`, so the exe can be moved or replaced freely.

### Releases and auto-update

`DANBOT.exe` updates itself at startup, before the deck opens: it fetches a
signed manifest (`Update/UpdateChannel.cs`), and when the release is newer it
downloads it, checks its size and SHA-256, swaps it in at the same path (so the
HidHide whitelist keeps matching) and restarts. Offline or on any error it
simply starts the current version. Dev builds (`HappyBot.exe`) never update.

To release:

1. Bump `Version` in `HappyBot.csproj` and `BuildInfo.cs` (they must match).
2. `dotnet publish HappyBot.csproj -p:PublishProfile=SingleExe`
3. `node tools/release.mjs --upload` signs `publish\DANBOT.exe` and uploads it
   and `latest.txt` to the `danbot-downloads` R2 bucket (needs `wrangler login`).

Manifests are signed with `%USERPROFILE%\.danbot\release-key.pem`, created by
`tools/release-key.mjs`. Keep a backup and never commit it: without it,
installed copies can no longer be updated automatically.

For development, `dotnet build HappyBot.csproj` still produces the regular
`bin\<Configuration>\net10.0-windows\HappyBot.exe`.

## Run

Double-click `DANBOT.exe`. It reads the physical DualShock 4 HID reports
directly and merges them into a single ViGEm Xbox controller. Close DS4Windows and any other
virtual-pad tool first, or the game sees a second controller.

Recommended startup order:

1. Connect the DS4 and hide it from the game with HidHide (see below).
2. Start `DANBOT.exe`.
3. Confirm the Dashboard shows **Virtual controller: Ready**, and that **Controller & Binds › Input bridge** reports Source `ON`.
4. Start or restart the game after the virtual controller exists.

The bot does not need administrator rights. Driver installation and HidHide configuration require administrator rights.

## HidHide Setup

Hide the physical Sony controller and add `DANBOT.exe` to HidHide's
whitelist so it can still read the hidden HID device. HidHide matches the full
path, so add the exe again after moving it. Do not whitelist the
game: it should see only HappyBot's merged output controller. Reconnect the
controller after changing HidHide settings.

## Controller Mapping

The verified in-game Xbox layout is:

| Controller input | Game action |
| --- | --- |
| LT | Guard mode |
| LB | Quick chat |
| RT | Heavy attack / parry |
| RB | Light attack |
| X | Guardbreak |
| B | Cancel heavy attack |
| A | Dodge |
| Left stick | Movement |
| Right stick | Guard direction |

HappyBot's internal output mapping is:

| Bot action | Virtual controller output |
| --- | --- |
| Space | A |
| Left mouse button | RB |
| Right mouse button | RT |
| Numpad 5 | X |
| C / Numpad 9 | LT |
| Numpad 4 / 6 / 8 | Right stick left / right / up |
| Arrow keys | Left stick |

These bot action names are internal codes; every one is sent as the
virtual controller input shown. The configured hold button (`HoldButton` in
`Config.ini`, `LT` by default) arms every guard reaction.

## Using The Control Deck

The window has five pages (switch with the sidebar or **Ctrl+1–5**) and a
commit bar along the bottom of every page.

| Page | What it holds |
| --- | --- |
| **Dashboard** | Start / Pause, readiness checks, the active LT behavior, quick toggles, live signal, and session counters. |
| **Combat** | Orange response, guard and LT reactions, and Legit mode with its fallback percentages. |
| **Heroes & Timing** | The hero picker, hero response, and every reaction delay. |
| **Controller & Binds** | Controller toggle binds, the input bridge state, **Test input**, and the controller mapping reference. |
| **Diagnostics** | Resolution calibration, Scan screen, the vision overlay, live detection, the last reaction, counters, telemetry, and app actions. |

Typical setup:

1. Set the game resolution in **Diagnostics › Calibration & overlay**.
2. Pick the hero being played in **Heroes & Timing**.
3. Enable only the features needed for the test.
4. Click **Start bot** on the Dashboard.

Switches apply to the running bot immediately. Numbers (delays and
percentages) stay pending until **Apply timings** in the commit bar; **Start
bot** also applies them. **Save profile** writes either kind of change to the
active profile. The commit bar shows "Timings applied/not applied" and "Profile
saved/not saved" separately; **⋯** offers Save as, Reload saved, and Delete
profile. The **?** button in the title bar opens the setup guide.

Important feature switches:

- **Auto block** controls automatic guard direction changes and is required by every LT reaction.
- **Auto parry** under Guard & primary reactions controls the LT parry path.
- **Auto deflect** controls directional dodge deflects.
- **Orange response** (`Unblockables`) controls orange detection and dodge responses.
- **Hero response** enables hero-specific behavior for the selected hero.

## Controller Toggles

**Controller & Binds** can bind DS4 buttons to toggle Auto dodge, Orange
parry, and Auto parry. Everything else is set from the control deck.

## License

DANBOT needs an account and a license key before the control deck opens. On
launch the deck shows a sign-in screen (with **Create an account** for new
users); once signed in it asks for the key (`DANBOT-XXXXX-XXXXX-XXXXX-XXXXX`).
After **Redeem key** the key is tied to that account, the PC is registered
against it and the Dashboard appears. Its readiness list and **Diagnostics ›
License & cloud profiles** show the license state afterwards.

- Keys are lifetime or last a set number of days, counted from redemption.
- Each key allows a set number of PCs (default 1).
- The app re-checks the license at sign-in and every 6 hours. Without a check
  it keeps working offline for up to 72 hours (never past the expiry date);
  after that, or when the license expires or is revoked, the deck goes back
  to the license screen, Start is refused and a running bot pauses until the
  license is valid again.
- Signing out removes the license from that PC until you sign in again.

The `license` edge function (`supabase/functions/license`) answers with a
token signed by an ECDSA P-256 key that it created on first use and keeps in
Supabase Vault. The app checks it with the public key in
`Cloud/SupabaseProject.cs` and stores it next to the login in
`%LOCALAPPDATA%\DANBOT\license.bin` (DPAPI). Only SHA-256 hashes of keys are
stored in `public.licenses`; clients cannot read or write the license tables.

Managing keys (Supabase dashboard › SQL editor):

```sql
-- Create keys; the plain keys are shown only here, so copy them.
select * from license_create(5);                    -- 5 lifetime keys, 1 PC each
select * from license_create(1, 30, 2, 'Sam');      -- 30 days, 2 PCs, with a note

select * from license_overview order by created_at desc;  -- who has what
select license_reset_devices('<license id>');             -- free the PC slots
select license_revoke('<license id>');                    -- stop a key
```

## Cloud Profiles

The same account syncs profiles between PCs. Local profile files stay the real
copy: syncing works the same offline or signed out, and signing out never
deletes them.

- **Sign in** downloads your cloud profiles and uploads the local ones.
- **Save profile** and **Save as** upload that profile; deleting a profile
  deletes it from the account too.
- If a profile changed in both places, the newer copy wins and the other is
  kept as a new `<name> backup` profile, so nothing is lost.
- Only the profile settings are synced; machine settings such as
  `HoldButton` stay on each PC.
- New accounts must confirm their email before the first sign-in.

The app ships only the project URL and its publishable key
(`Cloud/SupabaseProject.cs`). Row-level security on `public.bot_profiles`
limits every account to its own rows. The password goes over HTTPS and is
never stored; the rotating refresh token is kept in
`%LOCALAPPDATA%\DANBOT\cloud-session.bin`, encrypted with Windows DPAPI for
the current user.

## Vision Overlay

Use **Vision overlay** in Diagnostics (or the Dashboard quick toggles) to show DANBOT's diagnostic HUD. The overlay is click-through, hidden
from screen capture, and displays the anchor scan area, the green/yellow anchor,
the derived combat and guard regions, and the live reaction stage (for example,
`LEFT → GUARD`, `LEFT → PARRY READY`, then `LEFT → PARRY SENT`). It is hidden by
default and does not change detection or input behavior.

The Dashboard's **Live signal** shows `Hold gate: DOWN` when the configured hold
button is detected.

## Test Input

Click **Run next test** in **Controller & Binds** to cycle through diagnostic
actions:

- Dodge (A)
- Heavy (RT)
- Light (RB)
- Guard Break (X)
- Guard Top
- Guard Left
- Guard Right

Each test sends five inputs after a three-second delay. Pause the bot first.

## Development

```powershell
dotnet build HappyBot.csproj -c Debug                       # build
dotnet run --project tests\HappyBot.Tests\HappyBot.Tests.csproj   # C# tests
node --test "ui/tests/*.test.js"                             # UI tests (Node 22+)
node ui/dev/serve.js                                         # browser preview: http://localhost:5174/?fake
```

The browser preview runs the control deck against `ui/dev/fake-host.js`, a
stand-in for the bot, so the UI can be worked on without WebView2.

Architecture:

```text
ui/ (HTML/CSS/ES modules, served from https://danbot.app.example/)
  ⇅ JSON messages (App/Protocol, mirrored in ui/js/protocol.js)
MainForm         WebView2 host: window chrome, timers, message routing
  → DeckController   all control-deck behavior, UI-free and unit-tested
      → SettingsSession  draft vs live settings (switches live, timings on Apply)
      → CloudCoordinator account sign-in, license and profile sync (Cloud/, Supabase REST)
      → BotCore          detection and reactions
```

Design tokens live in `ui/styles/tokens.css` and are documented in `DESIGN.md`;
the vision overlay mirrors them in `Overlay/OverlayTheme.cs`.

UI protocol v1 — page → host: `ready`, `settings`, `start`, `apply`,
`toggle-pause`, `resolution`, `scan`, `test`, `profile-select`, `profile-load`,
`profile-save`, `profile-save-as`, `profile-delete`, `orange-parry`,
`vision-overlay`, `anchor-scan`, `bind-auto-dodge`, `bind-orange-parry`,
`bind-auto-parry`, `telemetry`, `export-telemetry`, `cloud-sign-in`,
`cloud-sign-up`, `cloud-sign-out`, `cloud-sync`, `license-redeem`, `howto`, `readme`, `reload`,
`minimize`, `close`, `drag`. Host → page: `init` (settings, status, protocol),
`status` (every 200 ms), `settings`, `apply-result`, `toast`, `dialog`.

## Legacy Input

The verified setup uses ViGEm and does not require the legacy Interception driver.

## Notes

- The original behavior reference is preserved in `archive/legacy-ahk/HappyBot.ahk`.
- Build output and generated `Config.ini` files are ignored by Git.
- Game automation can violate game rules or anti-cheat policies. Test responsibly, preferably in training or offline modes.
