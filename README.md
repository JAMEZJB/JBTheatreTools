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

### Managing the suite

The same features on every launcher (the few that don't apply to Android are marked *desktop*):

- **Find & filter** — a search box (⌘F / Ctrl+F; Esc clears) matches every word against each app's name,
  description and section, and a status filter shows All / Installed / Updates / Not installed, with a
  "3 of 24 apps" count. Reordering is paused while a filter is on.
- **Release notes in the launcher** — every release of an app, newest first, with its date and notes;
  releases newer than yours are marked "New since your version".
- **Download sizes and release dates** — each row shows how old the latest release is and how big its
  download is, and Update All / Download All show the total download.
- **Hold an app at its version** — Update All, automatic updates and update notifications leave a held app
  alone (it shows "Held" instead of "Update") until the hold is released.
- **Show lock** — for show time: installs, updates, roll backs, uninstalls and imports are paused, and so are
  scheduled checks and automatic updates, while launching still works (⌘L / Ctrl+L). Turning it off asks
  first. The command line honours it too.
- **One-click roll back** *(desktop)* — go back to the version you had before the last update; the app is
  then held there.
- **Activity history** — installs, updates, downgrades, uninstalls and failures, newest first, with times
  (⌘Y on macOS, Ctrl+H on Windows).
- **Export / import a setup** — save which apps (and editions) are installed, their versions, holds and
  the list layout to a small JSON file; importing it on another machine previews exactly what will be
  installed or skipped before anything happens. Any launcher reads any other's file.
- **Scheduled checks** — while the launcher is open it checks again every hour, 4 hours (default), 12 hours
  or once a day (with "Check for updates" set to Every launch). A check that fails in the background leaves
  the list as it was. On Android, background checks run on Wi-Fi.
- **Update notifications** *(opt-in)* — a system notification when a check finds new updates while the
  launcher is in the background, once per version. The system asks for permission only when you switch
  them on.
- **Automatic updates** *(desktop, opt-in)* — after each check, updates install on their own: never for
  held apps, apps that are open, or under show lock.
- **Cancel and Stop** — cancel a single download; Stop ends an Update All / Download All after the app that's
  installing.
- **Disk-space check** — before a download, the launcher makes sure there's room for it (and for unpacking
  it), and says how much more space is needed instead of failing half-way.
- **Storage** — how much the installed apps and the download cache take, and a button to clear the cache.
- **App details** — version, install date, location, size on disk, latest release and download size,
  previous version and hold state (desktop: with Show in Finder / Explorer).
- **Quick launch** — a menu-bar icon on macOS and a Launch submenu in the Windows tray (optionally always
  shown) open any installed app directly; on Android up to four installed apps, most recently opened
  first, become shortcuts on the launcher's icon.
- **Keyboard shortcuts** *(desktop)* — refresh (⌘R / F5), find, update all (⌘U / Ctrl+U),
  show lock, activity, settings (⌘, / Ctrl+,) and list / grid view (⌘1 ⌘2 / Ctrl+1 Ctrl+2).
- **Diagnostics** — one click copies (or on Android shares) a plain-text support report: versions,
  settings, every app's state and recent log lines. The token and passphrase are never included, and
  token-shaped strings in log lines are masked — read the report before sending it, as log lines can
  contain file paths.
- **What's new in the launcher** — after the launcher updates itself it offers, once, the notes of every
  release since the version you had.

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
| NDI Tools | `JAMEZJB/NDITools` | Light: `NDITools-macOS.zip`, `NDITools-Windows-{x64,arm64}.exe` · Full: `NDITools-Full-macOS-{arm64,x64}.zip`, `NDITools-Full-Windows-x64.exe` |
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

**Windows updates:** EXE and ZIP downloads are prepared in a separate folder before the installed
version is changed. Failed extraction or metadata publication leaves the previous installation
available. Shortcuts are replaced only after the new install is recorded; a shortcut or old-file
cleanup failure does not discard the new install. Old files that cannot be removed are retained.

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
