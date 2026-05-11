# SIDM End-to-End Test Checklist

A manual verification pass for the full system. Run this before tagging any
public release — automated tests cover ~95% of the engine, but the
extension flows + browser handshake + first-run UX can only be validated
in a real Chrome / Firefox.

Time budget: ~20 minutes for the full pass.

## 0. Setup (once)

```pwsh
# .NET side
dotnet build SIDM.sln
dotnet test SIDM.sln                                       # expect 203 / 203
dotnet publish src/SIDM.BrowserHost -c Release -r win-x64 --self-contained

# Extension side
cd src/SIDM.Extension.Chrome  && npm install && node build.mjs && cd ../..
cd src/SIDM.Extension.Firefox && npm install && node build.mjs && cd ../..

# Run SIDM once so it creates %LocalAppData%\SIDM\
dotnet run --project src/SIDM.App
# (close the window; we'll relaunch after the host is registered)
```

## 1. Native Messaging Host registration

```pwsh
src/SIDM.App/bin/Debug/net8.0-windows/SIDM.App.exe --hosts-status
src/SIDM.App/bin/Debug/net8.0-windows/SIDM.App.exe --register-hosts
src/SIDM.App/bin/Debug/net8.0-windows/SIDM.App.exe --hosts-status
```

**Expect:**
- Before register: both Chromium + Firefox lines say "NOT registered".
- After register: both say "registered → \<path to NMH manifest .json\>".
- Manifest files exist at `%LocalAppData%\SIDM\host\com.sidm.host.{chromium,firefox}.json`.

## 2. Chrome extension load

1. Launch Chrome.
2. `chrome://extensions/` → toggle Developer Mode.
3. **Load unpacked** → pick `src/SIDM.Extension.Chrome/dist/`.
4. Copy the extension ID from the card (e.g. `lmkpcnoddmlpkbkjcnnpbiglmgcjknlc`).
5. Re-run with the real ID:

   ```pwsh
   SIDM.App.exe --register-hosts --extension-id <paste-here>
   ```

**Expect:** the Chromium manifest now lists this exact ID under
`allowed_origins`. Without this step, `connectNative` fails silently.

6. Click the extension's toolbar icon. The popup appears. With no videos
   open it says "No video streams detected on this page."
7. Open the gear ⚙ → options page shows. Click **Test connection**.

**Expect:** "Connected to Snw Internet Download Manager" + the version
string. If "Native host not registered" — `--register-hosts` didn't run
or used the wrong ID.

## 3. Firefox extension load

1. Launch Firefox.
2. `about:debugging` → This Firefox → **Load Temporary Add-on…** → pick
   `src/SIDM.Extension.Firefox/dist/manifest.json`.
3. Click the SIDM toolbar icon → popup → ⚙ → **Test connection**.

**Expect:** same handshake. The Gecko ID is hard-pinned to `sidm@snw.dev`
so no per-extension-ID dance is needed.

## 4. Plain HTTP download capture

1. Launch SIDM (it should now be running; if not, `dotnet run`).
2. In Chrome, navigate to a small file, e.g.
   `https://github.com/sharkdp/bat/releases/download/v0.24.0/bat-v0.24.0-x86_64-pc-windows-msvc.zip`.
3. Chrome's "Save As" dialog should NOT appear — instead, SIDM's
   Add-download popup pops up with the URL, size, and MIME prefilled.

**Expect:**
- The size field shows the real size (Chrome already saw the `Content-Length`).
- The remembered-folder hint shows the default Downloads folder.
- Click **Start download** → progress dialog opens, chunks fill in,
  download completes, the file is on disk.

5. Repeat the same download. The folder should be remembered for `.zip`.

## 5. yt-dlp routing (Phase 4.A)

Prerequisite: `yt-dlp.exe` on PATH or configured under Settings → Video
downloader. `ffmpeg.exe` likewise (optional but recommended).

1. In Chrome, navigate to any YouTube video.
2. Right-click the page → **Download with SIDM** (context menu).
3. The Add-download popup shows the orange "Video URL detected — will
   download via yt-dlp" badge. Segments slider is hidden.
4. Click **Start download**.

**Expect:** progress dialog shows steady byte progress (yt-dlp reports
through the same sink as HTTP downloads). On completion, an `.mp4` lands
in the chosen folder.

If yt-dlp isn't configured: the row shows status `Failed` with a clear
"Open Settings → Video downloader" message. Settings → Video downloader →
**Test** button runs `yt-dlp --version` and shows the result.

## 6. HLS native engine (Phase 4.B)

Find a simple unencrypted HLS test stream — e.g.
`https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8` (Mux public test stream).

1. Open Chrome's devtools network panel and visit the page that hosts
   the stream (or paste the URL directly into SIDM via Add download).
2. With the toolbar popup approach: open the URL in a tab that auto-plays
   it. The SIDM extension badge increments to **1**.
3. Click the SIDM icon → popup → cyan **HLS** row with the URL.
4. Click the row.

**Expect:** progress dialog shows segments being fetched; on completion,
`.ts` lands in the folder. If ffmpeg is configured, `.ts` is replaced by
`.mp4` (Phase 4.B.2 auto-remux).

For AES-128 verification: use `https://test-streams.mux.dev/test_001/stream.m3u8`
which carries `EXT-X-KEY:METHOD=AES-128`. Decrypted output should be
playable in VLC.

## 7. DASH native engine (Phase 4.C)

Test stream: `https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd`
(Big Buck Bunny, video + audio + multiple bitrates).

1. Paste the `.mpd` URL into SIDM Add-download.
2. The teal **DASH manifest detected — video + audio tracks will be
   fetched and muxed to .mp4** hint appears.
3. Click Start.

**Expect with ffmpeg configured:** progress fills, then a single `.mp4`
with video + audio lands in the folder.

**Expect without ffmpeg:** two files (`out.video.mp4`, `out.audio.mp4`)
with an informational `Error` field telling the user how to enable
muxing.

## 8. Media sniffer popup (Phase 4.D)

This is the headline new feature; verify it on three sites:

1. **YouTube** — visit any video. The popup should NOT show an HLS / DASH
   manifest (YouTube serves DASH via signed URLs that yt-dlp handles
   better). The popup is empty; use the yt-dlp path from §5 instead.
2. **A news site that uses HLS** — e.g. BBC iPlayer (UK only) or any
   site whose video player is built on hls.js (search "hls.js demo"
   for a public sample). The popup should show one HLS row.
3. **A site that uses DASH** — DASH test pages on dash.js demo, e.g.
   `https://reference.dashif.org/dash.js/latest/samples/dash-if-reference-player/index.html`
   and pick a sample stream. The popup should show one DASH row.

Each click sends the URL through the existing IPC pipe, which routes by
URL kind to the right engine (HLS / DASH / direct).

## 9. Queue + bandwidth (Phase 2.A+B)

1. Settings → set **Max concurrent downloads** to 2 and **Bandwidth** to
   1024 KiB/s.
2. Open SIDM. Paste 5 different download URLs in rapid succession.

**Expect:**
- Two start immediately; three wait in `Queued` status with "(waiting
  for slot)" in the status bar.
- The aggregate download speed across the two running rows hovers near
  1 MiB/s (governor in effect). When one finishes, the next pending row
  starts and the cap re-shares.

## 10. Scheduler (Phase 2.D)

1. Settings → Schedule rules → Add a rule covering the next 2 minutes
   only (e.g. start = now + 1 min, end = now + 3 min, all days).
2. Start a slow download (large file) BEFORE the rule's start time.

**Expect:**
- For the first ~30s (outside the window) the download is paused (queue
  suspended).
- At the rule's start time (within ≤30s tick), the download resumes
  automatically.
- After the rule ends, the queue suspends again and the row goes back to
  paused. The scheduler-paused IDs are remembered, so a new rule starting
  again later will resume them.

## 11. Cross-restart resume (Phase 1.gaps)

1. Start a download. Wait for it to reach ~30%.
2. Kill SIDM (Task Manager → End Task on `SIDM.App.exe`).
3. Relaunch with `dotnet run --project src/SIDM.App`.

**Expect:** the row appears as `Queued`, the auto-resume hosted service
re-enqueues it, the engine resumes from the persisted segment offsets,
and the file completes with the same hash as a fresh run.

## 12. Packaging smoke test (Phase 5.A)

```pwsh
dotnet tool install -g vpk    # once
pwsh scripts/publish.ps1
```

**Expect:** `releases/SIDM-Setup.exe` exists. Double-click on a clean
machine → SIDM installs to `%LocalAppData%\SIDM\`, the Start menu shortcut
launches the app, and the first-run hook calls `NativeHostRegistration.Register()`
automatically (verify via `SIDM.App.exe --hosts-status` after install).

---

## What to flag in a regression

If any step deviates, capture in this order:
1. The user-facing symptom (one sentence).
2. `%LocalAppData%\SIDM\logs\sidm-<today>.log` — the last 100 lines.
3. For extension issues: Chrome → `chrome://extensions/` → SIDM → **Inspect
   service worker** → Console tab. Firefox: `about:debugging` → SIDM → Inspect.
4. For NMH issues: `SIDM.App.exe --hosts-status` output.

File the issue with these four pieces and it's almost always reproducible.
