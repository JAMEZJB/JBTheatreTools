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
| HeloControl | `JAMEZJB/HeloControl` | `HeloControl-macOS.zip`, `HeloControl-Windows-{x64,arm64}.exe` |
| Machine Inventory | `JAMEZJB/ShowMachinesInventory` | `MachineInventory-macOS.zip`, `MachineInventory-Windows-{x64,arm64}.exe` |
| Cisco Switch Tools | `JAMEZJB/CiscoSwitchTools` | `Cisco.Switch.Tools.macOS.universal2.zip`, `Cisco.Switch.Tools.{x64,arm64}.exe` |
| CiscoBrotherLabels | `JAMEZJB/CiscoBrotherLabels` | `CiscoBrotherLabels-macOS.zip`, `CiscoBrotherLabels-Windows-{x64,arm64}.exe` |

Asset names differ per app, so the launcher resolves the right one for the current OS/arch.

## Downloads

The launcher itself ships on **its own** Releases page: macOS `.app` (zipped) and Windows `.exe`.

## Auth

The catalog repos are **private**, so fetching their release assets needs GitHub authentication.
You paste a **fine-grained personal access token** (Contents: read) into the launcher once per
machine; it's stored in the macOS **Keychain** / Windows **Credential Manager** and used for both
the API and the asset downloads. Create one at github.com/settings/tokens. Built apps and any
downloaded payloads are gitignored and never committed; the token is never written to disk in
plaintext.
