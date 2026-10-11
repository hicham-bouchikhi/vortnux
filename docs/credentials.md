# Nexus credentials

Vortnux saves API keys only in your Linux Secret Service keyring. It keeps the key in memory while connected and sends it only to `api.nexusmods.com`. There is no hardcoded key and no new local key file.

## First connection

Install `libsecret` on Arch, or the package providing `secret-tool` on your distribution. Run and unlock a compatible Secret Service provider, such as GNOME Keyring or KWallet with Secret Service support enabled. Installing the CLI alone does not start or unlock a keyring.

Open **Settings → Personal Nexus API key**, enter the key obtained from your Nexus account settings, and click **Connect**. Vortnux validates it with Nexus, stores it in the keyring, and verifies it can read the same value back before completing the connection. Later launches retrieve and validate the saved key automatically.

## Storage and migration

The keyring item is labelled **Vortnux Nexus API key**. Its attributes identify Vortnux, Nexus Mods and the absolute state-directory path's SHA-256 digest. The provider's default collection holds the secret. Your desktop password/keyring manager can inspect that item.

The default state directory is `~/.local/share/vortnux`; `VORTNUX_STATE_DIR` overrides it. The file `credentials/storage` contains only the word `keyring`, not your API key. Its directory has mode 0700 and the marker has mode 0600.

Older builds may have stored the secret at:

```text
~/.local/share/vortnux/credentials/nexus-api-key
```

On this machine, that default path is `/home/esperadoce/.local/share/vortnux/credentials/nexus-api-key`. With a state-directory override, the legacy file is `<VORTNUX_STATE_DIR>/credentials/nexus-api-key`.

On the next launch, Vortnux migrates this file into the keyring. It deletes the file only after storing the key, verifying an exact read-back match, and recording the keyring marker. Failed migration retains the old file and stops reconnection; it does not use the file as a fallback. Unlock the keyring and click **Retry saved connection** to finish migration without typing the key again. Deleting the file does not remove copies in old backups or snapshots.

## Connection controls and troubleshooting

| Control or problem | Behavior or action |
| --- | --- |
| Retry saved connection | Retrieve the saved key again and retry Nexus validation without entering it. Also retries migration. |
| Disconnect | End this session; retain the saved key for future launches. |
| Forget saved key | Unlock and remove the keyring item, then delete the marker and any legacy file. If removal fails, unlock the keyring and retry. |
| Missing `secret-tool` or unavailable keyring | Install the CLI and start/unlock a compatible Secret Service provider. There is no plaintext fallback. |
| Temporary Nexus/network failure | Keep the saved key; retry after the connection recovers. |
| Revoked or expired Nexus key | Enter a replacement key and Connect. |
| Different state directory or computer | Connect there once; credentials are tied to that state-directory path and desktop keyring. |

Keys are excluded from SQLite, activity history and database backups. Restoring a database does not restore the credential. Avoid putting keys in terminal commands, screenshots or diagnostic reports.

The architectural decision and migration checks are recorded in [ADR 0001](adr/0001-nexus-api-key-storage.md).
