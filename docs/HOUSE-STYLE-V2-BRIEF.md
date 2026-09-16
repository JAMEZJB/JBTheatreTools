# House Style v2 — restyle brief for JB Theatre Tools (`theatre`)

Orchestrated by the GitHub controller, 2026-09-16. Approved by James Breedon. Canonical spec:
https://claude.ai/artifact/43qrvhqBqja8e9qzZsGZJz · decisions: `SuiteDesignReview/docs/house-style.md` ("HOUSE STYLE v2") ·
kit contract: `SuiteDesignReview/kit/README.md` · this repo's kit copy: `n/a (native)/house/` (kit v2.0.0 — DO NOT EDIT).

## Your parameters

| | |
|---|---|
| Repo | `~/GitHub/JBTheatreTools` (work in the worktree you were started in) |
| Task | NATIVE TOKEN SKIN — apply the v2 tokens to the SwiftUI + WinForms launcher; no structural or UX change |
| Skeleton | **native launcher (no skeleton)** — `<main class="app (native)">` |
| Accent (light → dark) | `#AF52DE` → `#C77BF0` · on-accent `#FFFFFF` → `#0B0F1E` |
| Header glyph | theatre masks (existing multi-tile icon motif stays) (redraw as a single white 2px-stroke outline on the 24-grid; NO wordmark text) |
| Tagline | Install, update & launch the JB tool suite |
| Version bump | set `APP_VERSION` to **1.23.0** (was 1.22.0) — the controller tags it |
| Web dir | `n/a (native)` |
| Verify | `bash build.sh && swift test (in JBTheatreTools/) ; bash build-win.sh && dotnet test (in JBTheatreToolsWin/)` |
| Screenshots | `docs/restyle-screenshots/theatre-light.png` + `theatre-dark.png` (method below) |
| Reference build | native token skin — see the launcher rules |

## What "done" means

1. The app loads `house/tokens.css → house/fonts.css → house/house.css → app.css` in that order; the old
   `style.css`/`styles.css` is deleted; `app.css` contains ONLY the four accent slots + genuinely app-specific
   rules (a grid the job needs, a canvas, a visualiser). Nothing in `house/` is edited.
2. The window uses the skeleton above with the kit's frame: `.toolbar` (app glyph + name, `.spacer`, the
   view switch / primary action / Settings), `.frame`, (`.sidebar` for A), `.main` (with `<header><h1>…</h1><p>…</p>`
   per view), `.footer` with the rule-28 credit `Created by: James Breedon & Claude Code · v1.23.0`.
3. Type: Inter via the kit classes/base (400/500/600 only — never 700); JetBrains Mono (`.mono`) for readouts,
   paths, addresses, timecode, channel values, hashes; tabular numerals where digits align. Six-step scale:
   `.t-hero` 40 mono · `.t-status` 15/600 · `.t-title` 15/600 · body 13 · `.t-small` 12 · `.t-label` 10.5 caps.
4. Components come from the kit: `.btn` (+`.primary` ONE per view, `.danger`, `.ghost`, `.icon`), `.field`,
   native `<select>` styled by the kit (slate chevron), `.seg` for 2–3-way switches, `.check`, `.status`+`.dot`
   (`ok|warn|fail|busy`), `.banner`, `.chip`, `.panel` (with `<h2>` caps label), `table.list`, `.filelist`, `.log`,
   `.overlay`+`.modal` for destructive confirms, `.empty`.
5. RETIRED — remove every instance: emoji in buttons/labels (→ a Tabler outline icon or words alone);
   the `<details>` accordion pile as layout (→ sidebar items / toolbar views); 11px grey "note" prose (→ `.t-small`
   or drop); card-with-shadow on every block (→ flat `.panel` hairlines; `--shadow` only on floating things);
   wordmark text inside the app glyph; any yellow.
6. Icons: copy only the `<symbol id="tabler-…">` glyphs you use from `~/GitHub/SuiteDesignReview/kit/icons.svg`
   into an inline `<svg style="display:none">` sprite at the top of `index.html`; reference with
   `<svg><use href="#tabler-…"/></svg>`. Do not ship the 2 MB library.
7. Rules that must survive: 20 status row (dot + `.t-status` text, mirrors errors) · 21 selector slate `#6E8299`
   everywhere, accent only for identity + active nav + primary button + focus · 24 four button tiers, one primary
   per view · 25 semantic ok/warn/danger/info only, orange warn never yellow · 26 the theme control is labelled
   **"Appearance"** with System / Light / Dark (System default) and sets `light|dark|system` on `<html>` ·
   28 the credit line · 8 destructive actions confirm via `.modal`.
8. Behaviour parity: every feature, bridge call (`window.pywebview.api.*`), keyboard path, setting and error
   path works exactly as before. Keep element ids/data hooks the JS relies on or update the JS in step. This is
   a re-skin of the presentation layer — never change engine code, and never change `Engine/tools/preflight.sh`,
   build scripts, launchers, locks or CI (controller-owned).
9. Light, Dark and System all render correctly (Dark is the hero — check it first).
10. Verify is green, both screenshots exist, `docs/RESTYLE-PROGRESS.md` is complete, and your worktree has no
    stray files (no `_preview.html` committed — it is gitignored; no screenshots outside `docs/restyle-screenshots/`).

## Launcher skin rules (native SwiftUI + WinForms — PUBLIC repo)
- This is a TOKEN SKIN, not a port and not a redesign: apply the v2 graphite neutrals (light + dark sets from `SuiteDesignReview/kit/tokens.css`), the retuned accent (`#AF52DE` light / `#C77BF0` dark), radius-by-role (6 controls / 9 panels / 12 windows), hairline separation instead of card shadows, the six-step type scale in weights (system font stays — Inter is NOT bundled natively), the slate `#6E8299` selector, semantic ok/warn/danger/info colours, and the rule-28 credit line. Keep every existing behaviour and layout: the categories/collapse/reorder list, drag UX (James-signed-off), pinning, install flows, .zip install, self-update, Settings — untouched in structure.
- Both platforms at parity: `JBTheatreTools/Sources/...` (SwiftUI) and `JBTheatreToolsWin/` (WinForms). Build: `bash build.sh` (universal .app) + `bash build-win.sh` (win x64/arm64 via dotnet); `swift test` and `dotnet test` must stay green. Bump Info.plist CFBundleShortVersionString + CFBundleVersion and csproj `<Version>` to 1.23.0.
- PUBLIC-REPO BOUNDARY: never add internal names, hostnames, passphrases or private-repo details; only catalog app names + the jbtheatretools host are authorised public. Do not touch catalog.json.
- Screenshot: not possible headlessly for a native app — describe the change per screen in RESTYLE-PROGRESS.md; James checks on screen.

## Screenshot method (no computer-use tools)

Render the app's browser harness in headless Chrome. Repos with `Engine/tools/make_preview.py` +
`preview_mock.js`: run `python3 Engine/tools/make_preview.py` → `n/a (native)/_preview.html` (a mock
`window.pywebview.api`). Repos without one: create the pair by cloning `~/GitHub/PSNTools/Engine/tools/make_preview.py`
+ `preview_mock.js` (a Proxy-based mock returning plausible data). Make the harness honour `?theme=light|dark`
by setting the class on `<html>` (harness-only code). Then:
```
CH="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
mkdir -p docs/restyle-screenshots
"$CH" --headless=new --disable-gpu --hide-scrollbars --window-size=1280,820 --screenshot="$PWD/docs/restyle-screenshots/theatre-light.png" "file://$PWD/n/a (native)/_preview.html?theme=light"
"$CH" --headless=new --disable-gpu --hide-scrollbars --window-size=1280,820 --screenshot="$PWD/docs/restyle-screenshots/theatre-dark.png"  "file://$PWD/n/a (native)/_preview.html?theme=dark"
```
Look at both PNGs (Read them) and fix what you see before you stop. Fonts must render as Inter / JetBrains Mono
(if they fall back, the `house/fonts.css` path is wrong).

## Progress file — `docs/RESTYLE-PROGRESS.md` (mandatory, written at every checkpoint)

Create it from the template below BEFORE touching any other file, and update it: after the inventory, after
each view/section lands, after verification, and before you stop for ANY reason (including running out of
context — write it first). A replacement agent resumes from it. Keep it factual and terse.

```markdown
# Restyle progress — JB Theatre Tools (House Style v2)
Role/model: <restyle-worker (sonnet) | restyle-porter (opus) | restyle-reviewer (opus)> · Brief: docs/HOUSE-STYLE-V2-BRIEF.md
## State: NOT-STARTED | IN-PROGRESS | VERIFYING | DONE | BLOCKED
## Checkpoints (append; newest last; UTC timestamps)
- 2026-09-16T00:00Z started; read brief + kit + current web/
## Inventory
(views/sections/features/bridge calls/settings found; for ports: the full native feature list, mac + win)
## Done
## Next
## Verification
(preflight/verify output summary · harness built · screenshot paths · themes checked)
## Concerns / decisions for the reviewer and the controller
## Review (restyle-reviewer)
(appended by the reviewer: checked / changed / concerns / verdict PASS | PASS-WITH-CONCERNS | FAIL)
```

## Hard rules

- Never `git commit`, `git push`, `git tag`, or switch branches. The controller commits after review.
- Never edit `n/a (native)/house/`, `Engine/tools/preflight.sh`, build scripts, launchers, locks, CI.
- No computer-use / screen-driving tools. No network fetches (fonts and icons are local).
- Final report to the orchestrator ≤ 15 lines: repo · worktree path (`pwd`) · state · verify result ·
  screenshot paths · concerns. Everything else goes in `docs/RESTYLE-PROGRESS.md`.
