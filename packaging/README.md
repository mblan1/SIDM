# Packaging

Per-release artifacts for the three Windows package channels. None of these
files get bundled into the app itself — they live here so the publishing
process has a single source of truth for what goes where.

## winget

`packaging/winget/` contains the three-file manifest set
(version + locale + installer) the winget repo expects under
`manifests/s/snw/SIDM/<version>/`.

Per release:
1. Bump `PackageVersion` in all three files.
2. Update `InstallerUrl`, `InstallerSha256`, `ProductCode`, and
   `ReleaseDate` in `snw.SIDM.installer.yaml`.
3. Update `ReleaseNotes` + `ReleaseNotesUrl` in
   `snw.SIDM.locale.en-US.yaml`.
4. Run `winget validate snw.SIDM.yaml` (with the other two next to it).
5. Open a PR to <https://github.com/microsoft/winget-pkgs> adding
   `manifests/s/snw/SIDM/<version>/` with these three files.

## Chocolatey

`packaging/chocolatey/` has a standard NuGet-style package layout.

Per release:
1. Bump `<version>` in `sidm.nuspec`.
2. Update `url64bit` + `checksum64` in `tools/chocolateyinstall.ps1`.
3. `choco pack packaging/chocolatey/sidm.nuspec` → produces
   `sidm.0.x.0.nupkg`.
4. `choco push sidm.0.x.0.nupkg --source https://push.chocolatey.org/`
   (requires an API key configured via `choco apikey`).

## snw.dev landing page

`docs/landing/index.html` + `styles.css` are a single-page static site
ready to drop into any static host (GitHub Pages, Cloudflare Pages,
S3, etc.). No JS, no build step — edit and deploy.

The "Download for Windows" button currently points at
`https://github.com/snw-dev/SIDM/releases/latest` which redirects to
whichever Setup.exe is the latest release asset.
