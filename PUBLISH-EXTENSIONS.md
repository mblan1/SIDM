# Publishing the SIDM browser extensions

Two stores, two uploads, ~30 minutes of form-filling per store + review wait.

| Store | Pay once | Review | Upload file |
|---|---|---|---|
| Chrome Web Store | $5 dev account | 1–7 days | `extension-uploads/SIDM-Extension-Chrome-0.1.0.zip` |
| Firefox Add-ons (AMO) | Free | Hours to a few days | `extension-uploads/SIDM-Extension-Firefox-0.1.0.zip` |

Both zips already built — re-run with:

```powershell
cd src\SIDM.Extension.Chrome  ; npm install ; npm run build
cd ..\SIDM.Extension.Firefox  ; npm install ; npm run build
# zips land in extension-uploads/
```

---

## Before you start either store

You'll be asked for the same metadata twice. Have it ready:

**Short description (132 chars max, Chrome):**
> Capture downloads from your browser and hand them to SIDM for fast multi-segment downloading with HLS and DASH video grabbing.

**Long description:**
> SIDM (Snw Internet Download Manager) is an open-source download manager
> for Windows. This extension is the browser-side capture half — it
> intercepts downloads your browser would handle and hands them to the
> SIDM desktop app, which fetches the file in 1–16 parallel segments with
> queue, schedule, and bandwidth controls.
>
> The extension also detects HLS (`.m3u8`) and DASH (`.mpd`) video
> manifests on every page; the toolbar icon shows how many streams are
> grabbable and the popup lets you download any of them with one click.
>
> The desktop app is a separate download from
> https://github.com/mblan1/SIDM/releases/latest — install it first; this
> extension is non-functional without it.
>
> Open source under MIT: https://github.com/mblan1/SIDM

**Category:** Productivity / Tools.

**Privacy policy URL:** `https://snw-sidm.netlify.app/#privacy`
(once you've deployed the landing page — see `DEPLOY.md`. The Privacy
section on that page covers every permission the stores will ask about.)

**Homepage:** `https://github.com/mblan1/SIDM`

**Support / contact:** `https://github.com/mblan1/SIDM/issues`

**Screenshots (1280 × 800 or 640 × 400 PNG):**
- The toolbar popup showing detected video streams
- The "Connection" card in the Options page (green, showing connected
  SIDM version)
- The desktop app receiving a captured download

Take these by running SIDM locally + the extension as unpacked, then
Win+Shift+S to crop. Both stores want at least one; up to five each.

---

## Chrome Web Store

1. **Register** at https://chrome.google.com/webstore/devconsole (one-time
   $5 fee, paid by card).
2. **New item** → upload `extension-uploads/SIDM-Extension-Chrome-0.1.0.zip`.
3. **Store listing** → paste the description, screenshots, category,
   homepage URL.
4. **Privacy practices** — the hardest part. Every permission needs a
   one-sentence justification. Copy these (matching what the landing
   page Privacy section says):

   | Permission | Justification |
   |---|---|
   | `downloads` | Intercept the file the browser is about to save and redirect it to the SIDM desktop app over Native Messaging. |
   | `cookies` | Forward the browser's authentication cookies for the download host to SIDM so it can fetch gated / signed URLs successfully. Cookies are read only for the host of the current download and sent over a local pipe — never to a remote server. |
   | `nativeMessaging` | Communicate with the user-installed SIDM.BrowserHost.exe bridge, which forwards the capture to the SIDM desktop app. |
   | `webRequest` / `webNavigation` / `tabs` | Detect HLS / DASH manifest URLs as pages load so the toolbar badge can show how many video streams are grabbable on the current page. |
   | `contextMenus` | Add a "Download with SIDM" right-click entry on links and pages. |
   | `notifications` | Show "Queued" and "Failed" toasts when a capture is accepted or rejected by the desktop app. |
   | `storage` | Persist the user's extension settings locally. |
   | `<all_urls>` host permission | The extension must be able to read cookies and detect manifests on whatever site the user is downloading from. SIDM has no way to know in advance which sites the user will use it on. |

   - **Single purpose:** "Capture browser downloads and forward them to the
     SIDM desktop app for fast multi-segment downloading."
   - **Data usage:** check "No data is collected" — the extension does not
     transmit anything to remote servers.

5. **Distribution → Public** → **Submit for review**.

Review takes 1–7 days. Once approved, your extension gets a stable URL
like `https://chromewebstore.google.com/detail/sidm/<id>` and a 32-char
**production extension ID** that's different from your unpacked-dev ID.

### Production extension ID + Native Messaging

After approval, copy the production extension ID and re-register the
Native Messaging Host so installed users actually connect:

```powershell
SIDM.App.exe --register-hosts --extension-id <PRODUCTION-ID>
```

For new installs, this needs to happen automatically on first run.
`NativeHostRegistration.Register()` (already called in
`App.OnFirstRun`) writes the manifest for the *unpacked-dev* ID. We need
to bake the production ID into that helper once you have it. Once you do,
the user gets a working extension from a single SIDM install.

---

## Firefox Add-ons (AMO)

1. **Register** at https://addons.mozilla.org/developers/ (free, just an
   email).
2. **Submit a new add-on** → "On this site" (vs. self-hosted) → upload
   `extension-uploads/SIDM-Extension-Firefox-0.1.0.zip`.
3. AMO runs an automatic validator first — should pass since the
   manifest is MV3-clean. If it flags anything, paste the error here and
   I'll patch.
4. **Distribution channel:** Listed.
5. **Source code:** AMO requires the source if you bundle/minify. We use
   esbuild but it's a transparent bundler — submit the source-tarball
   below as proof:

   ```powershell
   # Source tarball for AMO reviewer (excludes node_modules, dist)
   cd src\SIDM.Extension.Firefox
   Compress-Archive -Path manifest.json, package.json, package-lock.json, tsconfig.json, build.mjs, src, icons `
     -DestinationPath ..\..\extension-uploads\SIDM-Extension-Firefox-source-0.1.0.zip
   ```

   Upload this `*-source-*.zip` in the **Source code** section. Build
   instructions for the reviewer (paste into the form):

   > Requires Node 18+.
   > 1. `cd` into the extracted folder.
   > 2. `npm install`
   > 3. `npm run build`
   > 4. The output in `dist/` matches the submitted `.zip`.

6. **Metadata** — same description, screenshots, etc. as Chrome.
   Categories: Web Development > Developer Tools or Other > Tools.
7. **License:** MIT (matches the repo).
8. **Privacy policy URL:** same `https://snw-sidm.netlify.app/#privacy`.

Submit. AMO usually reviews in a few hours to a day for clean MV3
extensions.

Once approved, the listing is at `https://addons.mozilla.org/firefox/addon/sidm/`.

Firefox extensions use the `gecko.id` from `manifest.json`
(`sidm@snw.dev` — already set) as the stable identifier; you don't get a
new ID like Chrome does. The Native Messaging registration in SIDM.App
already targets that ID, so installed users should connect with no
follow-up.

---

## Microsoft Edge Add-ons (optional)

Edge runs Chrome extensions from the Chrome Web Store with no friction,
so a separate listing is *optional*. If you want SIDM featured in Edge's
catalog:

1. Register at https://partner.microsoft.com/dashboard/microsoftedge
   (free).
2. Upload the same `SIDM-Extension-Chrome-0.1.0.zip` (Edge accepts
   Chrome MV3 unchanged).
3. Same description, screenshots, privacy URL.

Review is 1–3 days.

---

## Updating later

Every release of the extension:

1. Bump `"version"` in both `manifest.json` files.
2. `npm run build` in each folder.
3. Re-zip `dist/` (the build script already wipes & recreates `dist/`).
4. Upload to each store dashboard (Chrome, AMO, Edge) as a new version.

No re-review on Firefox if validator is happy; Chrome usually shorter
re-review for patches.
