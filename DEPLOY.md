# Deploying SIDM

Three things to publish, in order:

1. **Installer (`Setup.exe`)** — built locally with `scripts/publish.ps1`.
2. **Release feed (`releases/`)** — published to GitHub Releases so the in-app
   updater can find new versions.
3. **Landing page (`docs/landing/`)** — auto-deploys to GitHub Pages on push to
   `main` via `.github/workflows/deploy-site.yml`.

Outcome: a public URL where users click **Download**, run the installer, and
the app auto-updates from then on.

---

## 1. Build the installer

One-time:

```powershell
dotnet tool install -g vpk
```

Every release:

```powershell
cd D:\Workspace\IDM_CLONE
.\scripts\publish.ps1
```

Output appears in `releases/`:

- `SIDMSetup.exe` — what users download
- `SIDM-<ver>-full.nupkg` — Velopack full package (feed payload)
- `SIDM-<ver>-delta.nupkg` — delta from the previous version (smaller upgrade)
- `RELEASES` — manifest pointing the updater at the latest `.nupkg`

To bump the version, edit `Version` in `src/SIDM.Core/AppInfo.cs` (or pass
`-Version 0.2.0` to the script) before running.

---

## 2. Publish the release feed (GitHub Releases)

GitHub Releases is the simplest host the in-app updater understands.

1. Push the repo to GitHub if you haven't already. Note the URL (e.g.
   `https://github.com/<you>/SIDM`).
2. Tag the commit you packed:
   ```powershell
   git tag v0.1.0
   git push --tags
   ```
3. Go to **Releases → Draft a new release**, pick the tag, drop **every file**
   from `releases/` into the asset uploader (Setup.exe + both .nupkgs +
   RELEASES), then **Publish release**.
4. In SIDM → Settings → Updates, set **Update feed URL** to your GitHub repo
   URL (e.g. `https://github.com/<you>/SIDM`). The updater auto-detects the
   GitHub URL pattern and routes to `GithubSource`.

After that, every launch (or **Check now** click) checks the latest release
and shows a tray balloon when a newer version is available.

> The `Update feed URL` field also accepts a plain HTTPS URL pointing at a
> directory that serves `RELEASES` + the `.nupkg` files — S3, Cloudflare R2,
> your own server, etc. Same payload, no GitHub required.

---

## 3. Deploy the landing page

The site lives in `docs/landing/` (plain HTML/CSS + the SIDM logo). A workflow
at `.github/workflows/deploy-site.yml` publishes it to GitHub Pages on every
push to `main`.

**One-time setup:**

1. Push the worktree branch to GitHub and merge to `main` (already done if
   you've been following along).
2. In the repo on GitHub: **Settings → Pages → Build and deployment** →
   **Source: GitHub Actions**.
3. Push any commit touching `docs/landing/**` (or run the workflow manually
   from the Actions tab). The Actions tab shows the deploy job; when it
   finishes, the **URL** is at the top of the env card —
   `https://<user>.github.io/SIDM/`.

**Custom domain (optional):**

1. Add a `CNAME` file in `docs/landing/` containing just your domain (e.g.
   `snw.dev` on a single line, no protocol).
2. Point a `CNAME` DNS record at `<user>.github.io`.
3. In **Settings → Pages**, set the custom domain and enable HTTPS.

The download button on the landing page already points at
`https://github.com/snw-dev/SIDM/releases/latest`. Replace `snw-dev/SIDM` in
`docs/landing/index.html` with your actual GitHub `owner/repo` if different.

---

## Verifying the full loop

1. Run the installer (`SIDMSetup.exe`) — should land in `%LocalAppData%\SIDM\`
   with a Start-menu shortcut.
2. Open Settings → set the feed URL → **Check now** → see "SIDM 0.1.0 is up
   to date" (or an Available message if you've pushed a newer release).
3. Bump `AppInfo.Version` → run `publish.ps1` → upload the new assets to a
   new GitHub Release → relaunch the installed SIDM → the tray balloon should
   pop within a few seconds: **"SIDM update available — click to open SIDM."**
4. Click the balloon → main window restores → Settings → **Install update
   and restart** → app relaunches at the new version.

---

## Signed builds (when you have a code-signing cert)

Unsigned builds work fine for personal install but trip SmartScreen for other
users. Once you have an Authenticode cert installed in `Cert:\CurrentUser\My`:

```powershell
.\scripts\publish.ps1 -CertificateThumbprint <SHA1-of-your-cert>
```

The script passes `--signParams` to `vpk pack`; signtool signs every shipped
exe/dll + Setup.exe.
