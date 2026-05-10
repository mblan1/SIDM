# SIDM browser extension (Chromium)

Captures downloads in Chrome / Edge / Brave and forwards them to the SIDM
desktop app via Chrome Native Messaging.

## Build

```sh
cd src/SIDM.Extension.Chrome
npm install
npm run build
```

Output goes to `dist/`. Load that folder as an unpacked extension:

1. Open `chrome://extensions/`
2. Toggle **Developer mode**
3. Click **Load unpacked** → select `src/SIDM.Extension.Chrome/dist/`
4. Note the **extension ID** Chrome assigns (32 lowercase letters)

## Wire it to SIDM

The Native Messaging Host registration tells Chrome to spawn
`SIDM.BrowserHost.exe` for this extension. From the repo root:

```powershell
# Replace <ID> with the extension id Chrome assigned in step 4 above.
Start-Process -FilePath src\SIDM.App\bin\Release\net8.0-windows\SIDM.App.exe `
    -ArgumentList "--register-hosts","--extension-id","<ID>" -Wait
```

Then open the extension's **Options** page (right-click the extension icon
in the toolbar). The "Connection" card should turn green and show the
running SIDM version.

## Commands

```sh
npm run build       # one-shot build
npm run watch       # rebuild on changes
npm run typecheck   # typescript only, no emit
```

## Layout

```
src/
├── background.ts   # service worker — Native Messaging port, downloads capture
├── options.html    # settings UI
├── options.ts      # settings UI logic
├── options.css     # dark/light themed styles
└── ipc.ts          # wire-format mirror of SIDM.Ipc (C# side)
manifest.json       # MV3 manifest
build.mjs           # esbuild orchestration
```

## Permissions explained

- `downloads` — to intercept and cancel browser-initiated downloads.
- `cookies` + `<all_urls>` — to forward authentication cookies to SIDM
  so it can replay the request authentically.
- `nativeMessaging` — to talk to `com.sidm.host`.
- `contextMenus` — for the "Download with SIDM" right-click entry.
- `notifications` — for "Queued" / "Failed" toasts.
- `storage` — to persist user settings.

## Updating the gitignored `dist/` for distribution

`dist/` is gitignored. The release pipeline (Phase 5) will pack it into a
`.zip` for the Chrome Web Store and re-publish.
