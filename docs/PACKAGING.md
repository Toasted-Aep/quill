# Quill — MSIX packaging & auto-update

## Build a signed MSIX

One-time: create (or reuse) the dev signing certificate:

```powershell
New-SelfSignedCertificate -Type Custom -Subject "CN=QuillDev" -KeyUsage DigitalSignature `
  -FriendlyName "Quill dev MSIX signing" -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
```

Then build (thumbprint from the command above):

```powershell
dotnet build src/Quill/Quill.csproj -c Release -p:Platform=x64 -p:Msix=true `
  -p:QuillCertThumbprint=<THUMBPRINT>
```

Output lands in `dist/msix/Quill_<version>_x64_Test/`:
- `Quill_<version>_x64.msix` — the signed package
- `Quill_<version>_x64.cer` — the public cert users must trust once
- `Install.ps1` / `Add-AppDevPackage.ps1` — helper installers that do both

Normal builds are unaffected: without `-p:Msix=true` the app stays unpackaged
(`WindowsPackageType=None`), exactly as before.

## Installing (self-signed)

Users double-click `Install.ps1` (right-click → Run with PowerShell). It
imports the cert into Trusted People and installs the package. Alternatively:
import the `.cer` manually (Local Machine → Trusted People), then double-click
the `.msix`.

For public distribution buy a code-signing cert (or ship via the Microsoft
Store, which signs for you) — self-signed is for development and sideloading.

## Auto-update via GitHub Releases

`dist/Quill.appinstaller` points at the GitHub release assets. Flow:

1. Bump `Version` in `Package.appxmanifest` (e.g. 1.0.1.0) and in the
   `.appinstaller` file (both attributes).
2. Build the MSIX as above.
3. Create a GitHub release tagged `msix-latest` (or update it) and upload BOTH
   `Quill_<version>_x64.msix` (renamed to `Quill_x64.msix`) and
   `Quill.appinstaller`.
4. Users install ONCE by opening the `.appinstaller` URL:
   `https://github.com/Toasted-Aep/quill/releases/download/msix-latest/Quill.appinstaller`
   Windows then checks that URL on every launch and updates automatically
   when the version number grows.

The `.quill` file association is declared in the manifest; opening a `.quill`
file launches the app (file-open handling can be wired later).
