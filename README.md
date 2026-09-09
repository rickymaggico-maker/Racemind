# RaceMind
AI Race Engineer for Windows.

## Product rules
- Desktop Windows application, not a web app.
- One initial installation, subsequent versions through in-app updates.
- User telemetry and profiles live under `%LOCALAPPDATA%\RaceMind\Data` and survive updates.
- No overlay while driving. RaceMind is designed to work silently during a stint and surface information in the garage/pit workflow.
- No fabricated telemetry: UI fields remain empty until real simulator data is available.
- Official logo: Concept 1 / The Racing Line only.

## Release flow
Push a semantic version tag such as `v0.0.1`. GitHub Actions builds Windows x64, packages RaceMind with Velopack, creates the installer/release and becomes the update source for installed clients.

## Branding assets
The selected Concept 1 / The Racing Line artwork is stored losslessly as Base64 source assets and restored during the Windows build. This prevents accidental redesign or substitution of the official RaceMind logo.
