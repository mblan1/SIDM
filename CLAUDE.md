# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

SIDM (Snw Internet Download Manager) is a Windows download manager: WPF UI on .NET 8, multi-segment HTTP engine, browser-extension capture for Chromium + Firefox, and video grabbing via yt-dlp + a pure-C# HLS/DASH engine. Source of truth for product layout is [README.md](README.md); release/publishing flow is in [DEPLOY.md](DEPLOY.md); store-submission specifics for the extensions live in [PUBLISH-EXTENSIONS.md](PUBLISH-EXTENSIONS.md).

## Build & test

Prereqs: .NET 8 SDK pinned in [global.json](global.json) (`8.0.420`, rollForward latestFeature), Node 18+ for the extensions, Windows 10/11 for runtime.

```pwsh
dotnet build SIDM.sln
dotnet test SIDM.sln                                # all three suites
dotnet test tests/SIDM.Core.Tests                   # one suite
dotnet test --filter "FullyQualifiedName~RangeProbe"  # one test / class
dotnet run --project src/SIDM.App                   # launch the WPF app
```

The repo runs with `TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true` (see [Directory.Build.props](Directory.Build.props)) — a warning fails the build. Package versions are centrally pinned in [Directory.Packages.props](Directory.Packages.props); do not add `Version="…"` on `PackageReference` in csproj files, add a `PackageVersion` entry there instead.

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs `dotnet restore/build/test` on `windows-latest` for push + PR to `main`.

## Releasing

```pwsh
dotnet tool install -g vpk                  # one-time
pwsh scripts/publish.ps1                    # uses AppInfo.Version
pwsh scripts/publish.ps1 -Version 0.2.0     # explicit
pwsh scripts/publish.ps1 -CertificateThumbprint <SHA1>   # signed build
```

The version source of truth is `Version` in [src/SIDM.Core/AppInfo.cs](src/SIDM.Core/AppInfo.cs) — `publish.ps1` reads it. `Directory.Build.props` has its own `<Version>` for assembly metadata; keep them in sync. Output lands in `releases/` (Setup.exe + full/delta `.nupkg` + `RELEASES` manifest), to be uploaded to GitHub Releases for the in-app Velopack updater.

## Browser extensions

```pwsh
cd src/SIDM.Extension.Chrome  ; npm install ; npm run build
cd src/SIDM.Extension.Firefox ; npm install ; npm run build
# dist/ folder = unpacked extension; extension-uploads/*.zip = store payload
```

After installing the app, register Native Messaging Host manifests once per user:

```pwsh
src/SIDM.App/bin/Debug/net8.0-windows/SIDM.App.exe --register-hosts
SIDM.App.exe --hosts-status      # see what's registered
SIDM.App.exe --unregister-hosts  # undo
```

The installer auto-runs `--register-hosts` via Velopack's `WithFirstRun` hook ([App.xaml.cs:OnFirstRun](src/SIDM.App/App.xaml.cs)). For the production Chrome Web Store ID (different from unpacked-dev ID), pass `--register-hosts --extension-id <PROD-ID>` — see [PUBLISH-EXTENSIONS.md](PUBLISH-EXTENSIONS.md).

## Architecture

The solution is a layered .NET 8 stack plus two browser extensions and a native-messaging bridge exe. There is a strict dependency direction: `SIDM.App` → `SIDM.Core` + `SIDM.Data` + `SIDM.VideoGrabber` + `SIDM.Ipc`; `SIDM.BrowserHost` → only `SIDM.Ipc`; `SIDM.Core/Data/Ipc/VideoGrabber` do not reference each other except `Data → Core` and `VideoGrabber → Core`. Keep it that way — `Core` must never depend on WPF, EF Core, or the IPC server.

### The download pipeline (read this first)

1. **Intake** — a download arrives from the UI (`AddDownloadDialog`) or IPC (`IpcDispatcher` invoked by `IpcPipeServer`, which delegates UI prompts via `IDownloadIntake` implemented by `DownloadsViewModel`).
2. **Queue** — `DownloadQueue` (App layer) gates concurrency (default 4, `queue.maxConcurrent` in settings). Pending ids park in memory; rows persist as `Queued`.
3. **Routing** — `DownloadEngine` (App) inspects the URL:
   - YouTube/Vimeo/Twitch/TikTok/dailymotion/facebook/instagram → **yt-dlp sidecar** (`IYtDlpRunner` in `SIDM.VideoGrabber`).
   - `.m3u8` → **native HLS engine** (`HlsDownloader`, with `M3U8Parser` + `HlsCrypto` for AES-128).
   - `.mpd` → **native DASH engine** (`DashDownloader` + `MpdParser`).
   - else → **multi-segment HTTP** via `SIDM.Core.Engine.DownloadOrchestrator`.
4. **Engine** — `DownloadOrchestrator` probes (`IRangeProbe`), splits the byte range (`SegmentSplitter`), runs N `SegmentWorker`s in parallel writing to a sparse file (`SparseFileWriter`), gated by `IBandwidthGovernor` (`TokenBucketGovernor`). Falls back to single-stream when the server lies about ranges, mid-flight or up-front. Supports resume from caller-supplied per-segment offsets.
5. **Progress** — workers report bytes through `ISegmentProgressSink` → adapted to `IDownloadProgressSink`. The App composes a `CompositeProgressSink` so progress fans out to (a) SQLite via the raw-ADO hot-path `SegmentProgressWriter` and (b) the in-process `UiProgressBus` that view-models subscribe to. Don't add UI dependencies into Core — go through `UiProgressBus`.
6. **Post-process** — HLS/DASH outputs are auto-remuxed `.ts → .mp4` by `FfmpegRemuxer` when `ffmpeg.exe` is configured in Settings.

### Cross-cutting App-layer services

`SchedulerService` (time-window rules → start/pause downloads), `DownloadAutoResumeService` (re-enqueue orphaned `Queued`/`Downloading` rows on launch), `GracefulShutdownService` (pause everything cleanly on exit), `UpdaterService` (Velopack check + apply), `CrashReportingService` (Sentry init), `TrayIconService` (WinForms `NotifyIcon` — `UseWindowsForms=true` is in the csproj only for this), `ThemeService`, `CategorySeeder` (IDM-style defaults on first run), `BrowserExtensionPresence` + `BrowserExtensionInstaller`. All composed in [src/SIDM.App/Composition/ServiceCollectionExtensions.cs](src/SIDM.App/Composition/ServiceCollectionExtensions.cs) — that file is the map of what's wired.

### Browser-extension path

`SIDM.Extension.Chrome` / `.Firefox` (TS + esbuild, MV3) → speak Native Messaging length-prefixed JSON over stdin/stdout to `SIDM.BrowserHost.exe`, which bridges those frames over a per-user named pipe (`PipeNameProvider.ForCurrentUser()`) to `IpcPipeServer` inside the running app. If the app isn't running, `SIDM.BrowserHost` attempts to launch it with a short retry backoff. Message contract is `SIDM.Ipc.IpcMessage` (polymorphic JSON: `hello` / `download` / `download-response` / `error`). `BrowserHost` must stay zero-dep beyond `SIDM.Ipc` so the bridge exe is small and fast to spawn.

NMH manifests are written under `%LocalAppData%\SIDM\host\com.sidm.host.{chromium,firefox}.json` and registered in HKCU (per-user, no admin) — see `NativeHostRegistration`. The Chromium extension ID is supplied via `--extension-id` (defaults to a placeholder); the Firefox ID is `sidm@snw.dev` from `gecko.id` in the manifest.

### Data layer

EF Core + SQLite at `%LocalAppData%\SIDM\sidm.db` (WAL mode). `AddSidmData` ([src/SIDM.Data/DataServiceCollectionExtensions.cs](src/SIDM.Data/DataServiceCollectionExtensions.cs)) registers `SqliteSchemaInitializer` as the first hosted service so the DB exists before any other startup writes. EF is used for everything *except* the hot-path: per-segment progress writes go through `SegmentProgressWriter` (raw `Microsoft.Data.Sqlite` ADO), batched on its own hosted service loop. If you're touching segment progress, change `SegmentProgressWriter`, not the repository.

### CLI mode

`SIDM.App.exe` is a `WinExe` but switches into CLI mode when invoked with `--register-hosts` / `--unregister-hosts` / `--hosts-status`. CLI mode skips the IHost/Serilog bootstrap, attaches to the parent console via `AttachConsole`, and `Environment.Exit`s with a code. Add new CLI verbs by extending `IsCliCommand` + `ExecuteCliCommand` in `App.xaml.cs`.

## State on disk

`%LocalAppData%\SIDM\`:
- `sidm.db` (+ `-shm`, `-wal`) — SQLite, WAL mode
- `logs/sidm-YYYYMMDD.log` — Serilog rolling daily, 14 days retained
- `host/com.sidm.host.{chromium,firefox}.json` — NMH manifests after `--register-hosts`

## Tests

Three xUnit suites under `tests/`:
- **SIDM.Core.Tests** — engine, IPC framing/serializer, scheduling, bandwidth governor, video grabber (M3U8 parser, AES-128, HLS downloader with fake HTTP client, ffmpeg remuxer wrappers, yt-dlp progress parser/URL detectors).
- **SIDM.Data.Tests** — EF repositories and the raw-ADO `SegmentProgressWriter`.
- **SIDM.IntegrationTests** — real-network tests against an in-process `HttpListener`: range support, range fallback, mid-flight reconnect, 503 backoff, cross-restart resume.

The repo's stated baseline is 0 build warnings and 100% pass. Don't merge with regressions to either.

## Conventions worth knowing

- `Nullable enable`, `ImplicitUsings enable` repo-wide; `AnalysisLevel=latest`. New code is expected to be null-annotated.
- WPF UI uses **WPF-UI** (Fluent) + **CommunityToolkit.Mvvm** (source-generated `[ObservableProperty]` / `[RelayCommand]`). View-models live in `src/SIDM.App/ViewModels`, dialogs in `src/SIDM.App/Views`.
- `SIDM.App` assembly is intentionally **not** named `SIDM` (avoids AV false positives on freshly-built exes); end users launch via Velopack-created Start menu shortcut. Don't rename it back.
- Resilience policies: HTTP uses `Microsoft.Extensions.Http.Polly` + `Polly` retry/backoff configured in `HttpClientServiceCollectionExtensions`. Don't open-code retry loops in the engine.
- Vendored sidecars (`yt-dlp.exe`, `ffmpeg.exe`) are **not committed** — they're configured in Settings → Video downloader and downloaded at pack/install time.
