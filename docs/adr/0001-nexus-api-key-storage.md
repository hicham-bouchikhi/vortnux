# ADR 0001: Store Nexus API keys only in the Linux keyring

Status: Accepted  
Date: 2026-10-11

## Context

Nexus account connections must survive application restarts. Earlier builds kept the key in memory, then used keyring storage with a plaintext local fallback. A later build retained a local copy to reconnect when the keyring was unavailable. The user explicitly requested keyring-only storage and removal of the local copy.

Native AOT and warnings-as-errors remain required. The credential adapter must keep secrets out of process arguments, SQLite, activity records, database backups and diagnostic output.

## Decision

Use Linux Secret Service through `secret-tool`, supplied by `libsecret`. Store the API key in the keyring's default collection. Send the key through redirected standard input without a trailing newline; use argument lists without a shell. Bound process output and apply a ten-second timeout to each keyring operation.

Identify the item with non-secret attributes: `application=io.vortnux.Vortnux`, `service=nexusmods`, and `profile=SHA256(UTF8(full state-directory path))` encoded as uppercase hexadecimal. Use the label `Vortnux Nexus API key`. Changing the state-directory path therefore selects a different credential item.

A successful save requires both storing the key and reading back an exact matching value. Persist only the non-secret `keyring` marker in `<state-directory>/credentials/storage`. Never create a local key file or fall back to a plaintext connection when the keyring is unavailable. Keep the key in process memory for authenticated requests during the session.

## Migration and failure handling

On startup, an existing `<state-directory>/credentials/nexus-api-key` takes precedence for migration. Validate its format, store it in the keyring, verify the read-back value, write the backend marker, then delete the legacy file. Failure at any earlier step preserves the file and stops reconnection. This is a migration safeguard, not a local-storage fallback. File deletion does not guarantee forensic erasure from disks, snapshots or older backups.

Reject symbolic links and oversized legacy files. Restrict the credential directory to mode 0700 and metadata/legacy files to 0600.

If the keyring is locked, missing or unavailable, show instructions to start/unlock it and use **Retry saved connection**. A temporary Nexus validation failure keeps the stored key for retry. **Disconnect** clears the session while retaining the saved key. **Forget saved key** requests unlocking before clearing the keyring item, then removes the marker and any remaining legacy file. A failed keyring removal retains the information needed to retry.

## Alternatives

| Alternative | Reason rejected |
| --- | --- |
| Session-only key | Requires entering the key after every restart. |
| Owner-only plaintext fallback or mirror | Conflicts with the user's explicit keyring-only preference. |
| Application-encrypted file | Introduces a separate encryption-key lifecycle instead of using the desktop credential store. |
| Runtime-generated D-Bus proxy | Adds complexity to the Native AOT integration; the existing CLI adapter is tested and sufficient for this Linux proof of concept. |

## Consequences

Persistent Nexus connections require `secret-tool` and a running compatible Secret Service keyring. Arch packaging requires `libsecret`; AppImage users provide it and the keyring service through their system. The keyring provider controls storage and unlocking; Vortnux does not configure it automatically.

Database backups do not contain the API key. Restoring the database on another computer, or using another state-directory path, requires connecting the key there. Diagnostic output must never print a secret or include an API key in a command line.

## Implementation and validation

The store is implemented in [NexusCredentials.cs](../../src/Vortnux.Core/NexusCredentials.cs); startup restore and retry are in [MainWindow.Credentials.cs](../../src/Vortnux.Desktop/MainWindow.Credentials.cs).

[CredentialTests.cs](../../tests/Vortnux.Tests/CredentialTests.cs) covers keyring persistence, absence of local secret writes, unavailable-keyring rejection, migration success and failure, mismatched read-back, legacy-file safeguards, forgetting, GUI restart/retry and stdin transport. All 95 automated tests pass with warnings as errors; Native AOT publish and AppImage/Arch payload smoke checks also pass. The sandbox cannot verify the user's live keyring; their existing local key migrates when they launch the updated app with an available keyring.

See the [credential guide](../credentials.md) for setup, paths and recovery.
