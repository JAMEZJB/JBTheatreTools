# Windows uninstall publication preservation

Uninstall now reads `installed.json` strictly and publishes the selected slot's
removal before deleting its payload or shortcuts. A read or atomic replacement
failure propagates to the existing GUI or CLI error handler. The previous
manifest, working payload, shortcuts and cached identity remain available.
Previously, replacement failure could be logged and swallowed after deletion,
leaving an installed record whose executable had already gone.

The successful publication remains the commit point. It invalidates resolution
caches and raises `ManifestChanged` while the old payload and shortcuts still
exist; cleanup follows. A sibling edition is retained. A missing slot does not
publish another manifest or raise the event. Payload deletion remains best
effort: a locked executable may remain after its install record is removed.

Seven regression cases in `InstallManagerIntegrationTests` cover refused
manifest replacement, refused reading, malformed JSON, publication before
cleanup, legacy single-file sibling preservation, absent-slot behavior, and
locked-payload cleanup. The three sharing-refusal cases require Windows and
return early on other platforms. They use synthetic files and inert shortcuts;
no executable is launched. Run the focused project gate with:

```text
dotnet test JBTheatreToolsWin/Core.Tests --filter FullyQualifiedName~InstallManagerIntegrationTests
```

This change retains the existing per-manager mutation lock and atomic manifest
writer. It does not add a cross-process transaction or guarantee deletion of an
in-use executable. Shortcut creation/removal implementation, optional user-data
removal, download verification and version policy are unchanged.
