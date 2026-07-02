# Windows Installer

The WinUI app is packaged as a signed MSIX with an App Installer file. Users install `ActivityManagement.appinstaller` from a GitHub release, and Windows checks that App Installer URI for updates when the app launches.

## Versioning

Create release tags as `vMAJOR.MINOR.PATCH`, for example `v1.2.3`. The GitHub workflow maps that tag to the MSIX version `MAJOR.MINOR.PATCH.RUN_NUMBER`, because MSIX requires four numeric version parts.

## Signing

The GitHub workflow signs the MSIX with a self-signed `.pfx` stored as repository secrets. This is the lowest-friction option for personal distribution.

- `WINDOWS_PFX_BASE64`: base64 text for the signing `.pfx`
- `WINDOWS_PFX_PASSWORD`: password for that `.pfx`

The public `.cer` certificate from the same signing certificate must be trusted on each machine that installs the app. Keep using the same certificate for future releases. Windows will reject updates signed by a different publisher.

Release builds publish `Install-ActivityManagement.ps1` next to the installer. Run that script from the folder containing the release assets. It trusts `ActivityManagement-Signing.cer` for the current user in both `Trusted Root Certification Authorities` and `Trusted People`, then opens `ActivityManagement.appinstaller`.

## Startup

The package manifest declares `ActivityManagementStartup` as a startup task. After the installed app has been launched once, Windows can run it automatically when the user signs in. Users can disable it from Task Manager or Windows Settings.
