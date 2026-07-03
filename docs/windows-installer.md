# Windows Installer

The WinUI app is packaged with Velopack. Users install the generated `Setup.exe` from a GitHub release. The installed app checks GitHub Releases for newer Velopack packages when it launches and every 30 minutes while running.

## Versioning

Create release tags as `vMAJOR.MINOR.PATCH`, for example `v1.2.3`. The GitHub workflow publishes the app with that semantic version and packages it with Velopack.

## Packaging

Velopack creates a per-user installer that does not require a trusted MSIX publisher certificate. Unsigned installers can still show Windows SmartScreen reputation warnings, but there is no package root-certificate install step.

Release builds publish the Velopack installer and update packages from `artifacts/velopack`.

## Startup

The app registers itself in the current user's Windows startup registry key after Velopack install and update hooks. Users can disable it from Task Manager or Windows Settings.
