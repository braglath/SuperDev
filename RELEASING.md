# Releasing Super Dev

Pushing a version tag builds Super Dev and attaches four packages to one GitHub Release:

- `Super.Dev-v1.1.0-win-x64.zip`
- `Super.Dev-v1.1.0-win-x64.msix`
- `Super.Dev-v1.1.0-win-x64.cer`
- `Super.Dev-v1.1.0-osx-arm64.dmg` (Apple Silicon)
- `Super.Dev-v1.1.0-osx-x64.dmg` (Intel)

The tag you push is the version. `v1.2.0` produces a release titled `Super Dev v1.2.0`. GitHub release filenames cannot contain a space, so the download names use a dot: `Super.Dev-v1.2.0-win-x64.zip`. The app inside is still `Super Dev.exe` or `Super Dev.app`. The workflow does not create or move tags.

These macOS builds are ad-hoc signed so Apple Silicon can launch the bundle. They are not notarized. A download from GitHub is still blocked by Gatekeeper until the app is notarized. The Windows zip is not Authenticode signed, so SmartScreen warns that it is not safe. The MSIX is signed with `WINDOWS_PFX_BASE64` when that secret exists. Without it, the workflow signs the package with a temporary certificate and publishes the matching `.cer`. A self-signed certificate is not a verified publisher. Windows installs the MSIX only after that `.cer` is trusted.

## Create a release

Commit the release workflow first, then tag that commit.

```bash
git tag v1.1.0
git push origin v1.1.0
```

GitHub Actions runs `.github/workflows/release.yml` for tags matching `v*.*.*`.

1. A Windows runner publishes a self-contained `win-x64` app, zips the single-file exe, and packs a separate MSIX from the loose publish output.
2. A macOS runner publishes `osx-arm64` and `osx-x64`, wraps each in `Super Dev.app`, and creates a DMG.
3. After the Windows zip, MSIX, public certificate, and both Mac packages exist, one job creates the GitHub Release and uploads them together.

If any build fails, the release is not created.

## Download

Open the repository **Releases** page. The `v1.1.0` release lists the three assets above. GitHub also adds source archives; those are not the app.

## Install

**Windows.** Extract `Super.Dev-v1.1.0-win-x64.zip` and run `Super Dev.exe`, or install `Super.Dev-v1.1.0-win-x64.msix`.

Windows SmartScreen says an unsigned exe is not commonly downloaded and is not safe. Choose **More info**, then **Run anyway**. That warning stays on every new unsigned download until the exe is signed with a code-signing certificate that Windows trusts.

The MSIX is a full-trust desktop package. It registers the `superdev:` protocol and, when the Windows SDK accepts it, the `.superdev.json` file type. Before installing a self-signed MSIX, double-click `Super.Dev-v1.1.0-win-x64.cer` and install it into **Local Machine → Trusted People**. A certificate from a public authority is what shows a verified publisher. An EV certificate clears SmartScreen immediately.

Create the self-signed certificate once, from the repo:

```bash
bash scripts/create-windows-dev-cert.sh
```

That writes `.certs/superdev.pfx`, `.certs/superdev.cer`, and `.certs/password.txt`. Those files are gitignored. Set `WINDOWS_PFX_BASE64` to the base64 of `superdev.pfx` and `WINDOWS_PFX_PASSWORD` to the password in `password.txt`. Every later MSIX then uses the same publisher, `CN=Super Dev`.

**Mac.** Open the DMG for your Mac. Apple Silicon uses `osx-arm64`. Intel uses `osx-x64`. Drag **Super Dev** onto the **Applications** shortcut, then open Super Dev from Applications.

Apple Silicon treats an unsigned app from a download as damaged, so the icon can show as broken after it is copied to Applications. The package script ad-hoc signs the app so the bundle itself is valid. A copy downloaded from GitHub is still quarantined, and macOS will keep calling it broken until the app is notarized with an Apple Developer ID.

To open the copy already in Applications:

```bash
xattr -dr com.apple.quarantine "/Applications/Super Dev.app"
codesign --force --deep --sign - "/Applications/Super Dev.app"
open "/Applications/Super Dev.app"
```

## Signing

Removing the Windows and Mac warnings requires paid certificates. Do not commit certificates or passwords. Store them as GitHub Actions secrets.

**Windows.** An Authenticode certificate (an EV certificate clears SmartScreen immediately; a standard certificate does so after the file builds reputation). Store the PFX as `WINDOWS_PFX_BASE64` and its password as `WINDOWS_PFX_PASSWORD`. The MSIX job signs with that certificate and copies the certificate subject into the package identity. Sign `Super Dev.exe` before the zip is created if the zip should be trusted as well. The MSIX signature does not sign the exe inside the zip.

**Mac.** An Apple Developer ID Application certificate. Sign `Super Dev.app` with the hardened runtime, notarize it with `notarytool`, staple the ticket, then build the DMG from that signed app. The packaging script already ad-hoc signs the bundle before the DMG is created; replace that identity with the Developer ID when the certificate is available.

Typical secrets:

- `WINDOWS_PFX_BASE64`
- `WINDOWS_PFX_PASSWORD`
- `APPLE_CERTIFICATE_P12_BASE64`
- `APPLE_CERTIFICATE_PASSWORD`
- `APPLE_TEAM_ID`
- `APPLE_ID`
- `APPLE_APP_SPECIFIC_PASSWORD`

## When a tag already exists

Do not delete or force-push the tag as the first step.

- The workflow failed and no Release was created: open the failed run in the Actions tab and re-run it. Re-running uses the workflow stored in the tagged commit.
- A Release for that tag already exists: the workflow keeps that release and uploads the three packages onto it. A package with the same filename is replaced. You do not need to delete the Release.
- The tag points at a commit that does not contain the fix: ship the fix under a new tag, for example `v1.1.1`, from the updated commit.

```bash
git tag v1.1.1
git push origin v1.1.1
```

Moving `v1.1.0` onto another commit rewrites a published tag. Do that only when you mean to, and only after anyone who already fetched the tag knows it will change.
