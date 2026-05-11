# SIDM browser extension (Firefox)

Captures downloads in Firefox and forwards them to the SIDM desktop app via
Firefox Native Messaging.

## Build

```sh
cd src/SIDM.Extension.Firefox
npm install
npm run build
```

Output goes to `dist/`. Load it as a temporary add-on:

1. Open `about:debugging#/runtime/this-firefox`
2. Click **Load Temporary Add-on…**
3. Select `src/SIDM.Extension.Firefox/dist/manifest.json`

The extension id is pinned in `manifest.json` to **`sidm@snw.dev`** via
`browser_specific_settings.gecko.id`, which is what `NativeHostRegistration.cs`
already allow-lists in the Firefox NMH manifest. No per-load re-registration
needed (unlike Chromium, where the install assigns a random id).

> **Permanent install:** Firefox requires signed XPI for permanent install
> outside Nightly/Developer Edition. Phase 5 will sign through AMO; until then
> use Developer Edition with `xpinstall.signatures.required=false`, or load
> temporarily via `about:debugging` (cleared on browser restart).

## Wire it to SIDM

If you haven't already registered the Native Messaging Host (the same
registration covers Chromium and Firefox), from the repo root:

```powershell
Start-Process -FilePath src\SIDM.App\bin\Release\net8.0-windows\SIDM.App.exe `
    -ArgumentList "--register-hosts" -Wait
```

Then open the extension's **Preferences** (`about:addons` → SIDM → Preferences).
The "Connection" card should turn green and show the running SIDM version.

## Commands

```sh
npm run build       # one-shot build
npm run watch       # rebuild on changes
npm run typecheck   # typescript only, no emit
```

## Layout

```
src/
├── background.ts   # event page — Native Messaging port, downloads capture
├── options.html    # settings UI
├── options.ts      # settings UI logic
├── options.css     # dark/light themed styles
└── ipc.ts          # wire-format mirror of SIDM.Ipc (C# side)
manifest.json       # MV3 manifest with gecko id
build.mjs           # esbuild orchestration
```

## How this differs from the Chromium extension

| | Chromium | Firefox |
|---|---|---|
| Extension id | Assigned by browser at load time | Pinned to `sidm@snw.dev` |
| Background | `service_worker` (MV3) | `scripts` (MV3 event page) |
| NMH manifest key | `allowed_origins` | `allowed_extensions` |
| Registry key (HKCU) | `Software\<Browser>\NativeMessagingHosts\com.sidm.host` | `Software\Mozilla\NativeMessagingHosts\com.sidm.host` |
| Load (dev) | `chrome://extensions` → Load unpacked | `about:debugging` → Load Temporary Add-on |
| Permanent install | Chrome Web Store (or `.crx`) | Signed XPI via AMO (Phase 5) |

The runtime calls a Chromium polyfill: Firefox exposes `chrome.*` alongside
`browser.*` for MV3 extensions, so `background.ts`, `options.ts`, and `ipc.ts`
are byte-identical to the Chromium variant apart from the client-name string.

## Permissions explained

Same set as the Chromium extension — `downloads`, `cookies` + `<all_urls>`,
`nativeMessaging`, `contextMenus`, `notifications`, `storage`. See the Chromium
README for the rationale.

## Updating the gitignored `dist/` for distribution

`dist/` is gitignored. The release pipeline (Phase 5) will pack it into a signed
`.xpi` for AMO.
