# JBTheatreTools

A single desktop **launcher / installer** for James Breedon's app suite — one app that
downloads, installs, updates, and launches the tools from their GitHub Releases, so a machine
can get the whole toolkit from one place.

Native desktop apps for **macOS + Windows**, kept at parity (mac SwiftUI + Windows WinForms),
built by James Breedon & Claude Code.

> **Status: v1**, macOS + Windows at parity.

## What it does

- Show the catalog of tools and their latest released versions.
- Download the right asset for the current OS/arch from each tool's GitHub **Releases**, then
  install / launch it, with update checks.

## App catalog (the installable apps it launches)

| App | Repo | Download assets |
|-----|------|-----------------|
| HELO Control | `JAMEZJB/HeloControl` | `HeloControl-macOS.zip`, `HeloControl-Windows-{x64,arm64}.exe` |
| Cisco Switch Tools | `JAMEZJB/CiscoSwitchTools` | `Cisco.Switch.Tools.macOS.universal2.zip`, `Cisco.Switch.Tools.{x64,arm64}.exe` |
| Cisco Brother Labels | `JAMEZJB/CiscoBrotherLabels` | `CiscoBrotherLabels-macOS.zip`, `CiscoBrotherLabels-Windows-{x64,arm64}.exe` |
| Machine Inventory | `JAMEZJB/ShowMachinesInventory` | `MachineInventory-macOS.zip`, `MachineInventory-Windows-{x64,arm64}.exe` |
| Network Port Map | `JAMEZJB/NetworkPortMap` | `NetworkPortMap-macOS.zip`, `NetworkPortMap-Windows-{x64,arm64}.exe` |
| Projector Control | `JAMEZJB/ProjectorControl` | `ProjectorControl-macOS.zip`, `ProjectorControl-Windows-{x64,arm64}.exe` |
| Show Dashboard | `JAMEZJB/ShowDashboard` | `ShowDashboard-macOS.zip`, `ShowDashboard-Windows-{x64,arm64}.exe` |
| Show Handbook | `JAMEZJB/ShowHandbook` | `ShowHandbook-macOS.zip`, `ShowHandbook-Windows-{x64,arm64}.exe` |
| PSN Tools | `JAMEZJB/PSNTools` | `PSNTools-macOS.zip`, `PSNTools-Windows-{x64,arm64}.exe` |
| DMX Tools | `JAMEZJB/DMXTools` | `DMXTools-macOS.zip`, `DMXTools-Windows-{x64,arm64}.exe` |
| Desk Convert | `JAMEZJB/DeskConvert` | `DeskConvert-macOS.zip`, `DeskConvert-Windows-{x64,arm64}.exe` |
| NDI Tools | `JAMEZJB/NDITools` | Standard: `NDITools-macOS.zip`, `NDITools-Windows-{x64,arm64}.exe` · Full: `NDITools-Full-macOS-{arm64,x64}.zip`, `NDITools-Full-Windows-x64.exe` |
| PowerCalc | `JAMEZJB/PowerCalc` | `PowerCalc-macOS.zip`, `PowerCalc-Windows-{x64,arm64}.exe` |
| Show Control Tools | `JAMEZJB/ShowControlTools` | `ShowControlTools-macOS.zip`, `ShowControlTools-Windows-{x64,arm64}.exe` |
| ShowNet Scanner | `JAMEZJB/ShowNetScanner` | `ShowNetScanner-macOS.zip`, `ShowNetScanner-Windows-{x64,arm64}.exe` |
| Timecode Tools | `JAMEZJB/TimecodeTools` | `TimecodeTools-macOS.zip`, `TimecodeTools-Windows-{x64,arm64}.exe` |
| PDF Tools | `JAMEZJB/PDFTools` | Light: `PDFTools-macOS.zip`, `PDFTools-Windows-x64.exe` · Full: `PDFTools-Full-macOS-{arm64,x64}.zip` (no Windows Full yet) |

Asset names differ per app, so the launcher resolves the right one for the current OS/arch. An app listed
ahead of its first release shows "No release" and becomes installable once a build ships.

## Downloads

The launcher itself ships on **its own** Releases page: macOS `.app` (zipped) and Windows `.exe`.

**Verified downloads:** every app the launcher installs (and its own updates) is checked before it can run.
The download's size and SHA-256 must match the release's `SHA256SUMS`, and that manifest must carry a valid
**minisign** signature — made offline with the suite's release key, whose public half is embedded in the
launcher — naming that exact release. A tampered download, a swapped file, or a release that wasn't signed
with that key is refused. Installing an older version from the version picker still requires a valid
signature when one is published; releases that predate signing are allowed with an "unsigned" warning.
(This logic is unit-tested against a real signed release: `swift test` in `JBTheatreTools/`, and
`dotnet test JBTheatreToolsWin/Core.Tests`.)

**First launch on macOS:** if you downloaded JB Theatre Tools from the browser, Gatekeeper may block it
the first time (it's ad-hoc signed, not notarized). Right-click the app → **Open** → **Open** once; after
that it launches normally. Apps you install **through** JB Theatre Tools are unaffected.

## Auth

The catalog repos are **private**, so downloads need authentication. By default the launcher uses
its **built-in download server**: enter the suite **passphrase** once (ask the suite owner) and
you're done — **no GitHub token is needed on the machine**. A **GitHub token** mode (paste a
fine-grained PAT, Contents: read) remains available in Settings → Download access, and machines
that already have a token keep using it.

Either secret is stored in the macOS **Keychain** / Windows **Credential Manager**, never written
to disk in plaintext, and never logged. Built apps and any downloaded payloads are gitignored and
never committed.
