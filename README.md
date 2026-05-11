# SIDM — Snw Internet Download Manager

A fast, modern Windows download manager. Multi-segment HTTP downloads, queue
+ bandwidth governor + time-window scheduler, browser-extension capture
(Chromium + Firefox), YouTube/Vimeo via yt-dlp, and a pure-C# HLS engine
(with AES-128 decrypt) for sites that yt-dlp can't help with.

Built on .NET 8 + WPF (Fluent UI), SQLite, Velopack updates.

## Project layout

```
src/
  SIDM.App            WPF UI, IPC pipe server, NMH registration CLI
  SIDM.Core           download engine, abstractions, IPC types, scheduling
  SIDM.Data           EF Core + SQLite, repositories, hot-path raw-ADO writer
  SIDM.Ipc            shared zero-dep IPC protocol
  SIDM.BrowserHost    stdio↔pipe bridge for the browser Native Messaging Host
  SIDM.VideoGrabber   yt-dlp sidecar + native HLS engine + ffmpeg remux
  SIDM.Extension.*    Chromium + Firefox MV3 extensions (TypeScript + esbuild)
tests/
  SIDM.Core.Tests          unit tests — engine, IPC, scheduler, video grabber
  SIDM.Data.Tests          EF + raw-ADO progress writer tests
  SIDM.IntegrationTests    real-network tests via in-process HttpListener
scripts/
  publish.ps1              builds the Setup.exe installer (Phase 5.A)
```

## Build & run

Prerequisites: .NET 8 SDK, Windows 10/11.

```pwsh
git clone https://github.com/<you>/SIDM
cd SIDM
dotnet build SIDM.sln
dotnet test SIDM.sln
dotnet run --project src/SIDM.App
```

## Browser extension wiring

The extensions talk to SIDM through a per-user named pipe via a Native
Messaging Host bridge. After installing SIDM, register the host manifests
once per user:

```pwsh
src/SIDM.App/bin/Debug/net8.0-windows/SIDM.App.exe --register-hosts
```

The installer (`scripts/publish.ps1`) does this automatically on first run
via the `Velopack.WithFirstRun` hook.

To see what got registered:

```pwsh
SIDM.App.exe --hosts-status
```

To undo:

```pwsh
SIDM.App.exe --unregister-hosts
```

## Video downloads

YouTube / Vimeo / Twitch / TikTok / dailymotion / facebook.com/watch /
instagram.com/reel are auto-routed through **yt-dlp**. URLs ending in
`.m3u8` are auto-routed through the **native HLS engine** (no external
dependency). When `ffmpeg.exe` is configured, HLS outputs are auto-remuxed
from `.ts` to `.mp4` with stream copy.

Configure paths in **Settings → Video downloader**. The "Test" button next
to yt-dlp runs `yt-dlp --version` to verify the configured binary.

## Publishing a release

```pwsh
# One-time:
dotnet tool install -g vpk

# Each release:
pwsh scripts/publish.ps1                  # uses AppInfo.Version
pwsh scripts/publish.ps1 -Version 0.2.0   # explicit
```

Output lands in `releases/`:

```
releases/
  Setup.exe                 ← double-click to install
  SIDM-<ver>-full.nupkg     ← Velopack full package
  SIDM-<ver>-delta.nupkg    ← delta from the previous version
  RELEASES                  ← manifest pointing the updater at the latest .nupkg
```

To enable auto-updates, host `RELEASES` + the `.nupkg`s on a static URL
(GitHub Releases, S3, your own server) and configure the URL in the app's
update settings (Phase 5.C — not wired yet).

## Tests

The project keeps 0 build warnings and 100% passing tests.

```pwsh
dotnet test SIDM.sln
```

Three suites:

- **SIDM.Core.Tests** — unit tests for the engine (range probe, splitter,
  segment worker, orchestrator), IPC protocol + framing, native-host manifest
  builders, bandwidth governor, scheduler evaluator, category matcher, video
  grabber (yt-dlp progress parser, URL detectors, M3U8 parser, AES-128
  decrypt, HLS downloader with a fake HTTP client, ffmpeg remuxer).
- **SIDM.Data.Tests** — EF Core + raw-ADO progress writer.
- **SIDM.IntegrationTests** — real-network tests against an in-process
  `HttpListener` covering range support, range fallback, mid-flight reconnect,
  503 backoff, cross-restart resume.

## State on disk

The app writes to `%LocalAppData%\SIDM\`:

```
sidm.db                                  SQLite (WAL mode)
logs/sidm-YYYYMMDD.log                   Serilog rolling
host/com.sidm.host.{chromium,firefox}.json   NMH manifests (once registered)
```

## License

Closed-source distribution; license TBD before public v1.0.
