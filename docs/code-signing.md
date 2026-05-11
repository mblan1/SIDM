# Code signing

Public SIDM releases ship with an Authenticode signature on every exe + dll
inside the package, plus the generated `Setup.exe`. Without a signature
Windows SmartScreen quarantines unknown installers behind a "Microsoft
Defender SmartScreen prevented an unrecognized app from starting" dialog,
which kills the first-run conversion rate.

## Once

1. **Obtain a code-signing certificate.** Two common paths:
   - **OV (Organization Validation)** — cheap (~$60–250/yr from
     Sectigo / SSL.com / DigiCert), signs the binary but does NOT
     bypass SmartScreen reputation. You'll still see the "this is from
     a new publisher" prompt for the first few hundred installs.
   - **EV (Extended Validation)** — physical USB token (~$300–500/yr).
     Bypasses SmartScreen reputation from day one. Recommended for
     anything resembling a real product launch.

2. **Install the cert.** EV: plug the USB token, install the vendor's
   middleware, the cert auto-registers under
   `Cert:\CurrentUser\My`. OV: import the .pfx into the same store.

3. **Grab the SHA-1 thumbprint.** Powershell:

   ```pwsh
   Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey } |
       Select-Object Subject, Thumbprint, NotAfter
   ```

## Each release

```pwsh
pwsh scripts/publish.ps1 `
    -Version 0.2.0 `
    -CertificateThumbprint <40-hex-char-thumbprint-here>
```

The script forwards a `--signParams` value to `vpk pack`:

```
/sha1 <thumbprint> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256
```

Which makes vpk drive `signtool sign` on every shipped binary plus the
final installer. The RFC 3161 timestamp means the signature is still
valid after the cert itself expires.

## Verifying

```pwsh
Get-AuthenticodeSignature releases\SIDM-win-Setup.exe
```

Status must be `Valid`. `NotSigned` or `HashMismatch` means the publish
flow didn't sign or something tampered with the file after.

## Troubleshooting

- **"SignTool Error: No certificates were found that met all the given criteria."**
  Wrong thumbprint, or the cert isn't under `CurrentUser\My`. Re-check
  with `Get-ChildItem Cert:\CurrentUser\My`.
- **"The specified timestamp server either could not be reached…"**
  Network or DNS issue. Try `http://ts.ssl.com` as a fallback.
- **EV USB token wants a PIN every time.** Expected; some tokens cache
  it for the session if the vendor's middleware is configured.

## Firefox extension signing

A different concern: Firefox refuses to permanently install an unsigned
MV3 extension. AMO (addons.mozilla.org) signs the .xpi for free. The
extension's pinned gecko id (`sidm@snw.dev`, see Phase 3.D) lets the
NMH manifest's `allowed_extensions` array match the signed extension.

Submit at <https://addons.mozilla.org/developers/> using the same id.
