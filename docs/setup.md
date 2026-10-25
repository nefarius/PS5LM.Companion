# Console setup

`setup` prepares a jailbroken console that runs only Payload Manager. Every step can be repeated: what is already there is
skipped.

```sh
dotnet PS5LM.Companion.App.dll setup --host <console> --variant ps5lm|ps5lmplus [options]
dotnet PS5LM.Companion.App.dll setup --verify-manifest
```

| Option | Purpose |
|---|---|
| `--host` | Console IP or host name (required) |
| `--variant` | `ps5lm` (`PPSA99575`) or `ps5lmplus` (`PPSA99581`). Without it a terminal is asked; otherwise use `--skip-app` |
| `--skip-app` | Payloads only: no app install, no OnionHEN allow-list edit |
| `--upgrade-payload-manager` | Upgrade an older Payload Manager without a prompt |
| `--force` | Upload payloads and extra files again, reinstall the app |
| `--payload-manager-url` | When Payload Manager is not on port 8084 of the host |
| `--ftp-port` | ftpsrv port, default 2121 |
| `--manifest <file>` | Use another manifest (same JSON shape as `setup-manifest.json`) |
| `--cache <dir>` | Download cache, default `{Companion__DataDirectory}/setup-cache` |
| `--verify-manifest` | Download every artifact and check its SHA-256; needs no console |

In a container, mount `/data` so downloads are cached, and pass `--upgrade-payload-manager` because there is no terminal to ask.

## Steps

1. Payload Manager must answer on port 8084 and be 0.6.0 or newer. An older one is upgraded (below), after consent.
2. List what Payload Manager stores and compare it with the manifest.
3. Download missing payloads, verify the SHA-256, upload each as `<id>.elf` (slot `local/<id>`).
4. Enable autoload with: elfldr, kstuff-lite, 3 s pause, OnionHEN, 3 s pause, ftpsrv, ShadowMountPlus, VoidShell, PS5 Web File
   Manager. Entries you already had are kept after these.
5. Launch them in that order, skipping any that already run, waiting for each one's port or process. If elfldr does not come up,
   launching stops: Payload Manager sends everything through elfldr and cannot start it.
6. Place extra files over FTP (VoidShell's `index.html` to `/data/voidshell/www/index.html`). Uploaded as `.part`, then renamed.
   Skipped when the size on the console matches (FTP cannot hash).
7. Add the app's title ID to `exact_title_ids` in `[app_jailbreak]` of `/data/OnionHEN/config.ini`, after writing `config.ini.bak`.
   A missing file, section or key, or a bracketed list, is only a warning and nothing is changed.
8. Install the app. A new install is uploaded to `/data/ps5lm-companion/staging/<title>` and renamed into `/data/homebrew/<title>`, so
   ShadowMountPlus never scans a half-copied app. An installed app with the same `param.json` is left alone. A different version
   is overwritten in place with `param.json` last; nothing is deleted, so models in the folder stay.
9. Wait up to 3 minutes for ShadowMountPlus to register the title (VoidShell's library, else its log
   `/data/shadowmount/debug.log`). Nothing can force a scan; ShadowMountPlus pauses while a game runs.
10. Report: Payload Manager, autoload, each payload (port or process), the VoidShell dashboard page.

## Payload Manager upgrade

Payload Manager 0.5.x and 0.6.x look in `/data/pldmgr/payloads/pldmgr/` at startup for a newer `pldmgr*.elf` and hand off to it.
Setup uploads `pldmgr_v0.6.3.elf` (the `_vX.Y.Z` suffix is what puts it in that folder), which makes the upgrade survive reboots.
To switch now it then:

1. makes sure elfldr listens on 9021 (stores and launches it through the old manager if not);
2. only then shuts the old manager down and waits for port 8084 to close;
3. sends the new file to elfldr on 9021 and waits up to 60 s for `/version`.

If step 3 fails, Payload Manager stays down until the console starts its autoloader again, then it comes up as the new version.
Setup says so and exits with 2. If step 1 fails, nothing is shut down.

## The manifest

`src/PS5LM.Companion.Core/Setup/setup-manifest.json` is embedded in the build. Each entry has a direct GitHub release URL and the
SHA-256 from the release asset. A payload can list `files` (URL, SHA-256, absolute target under `/data/`) that go over FTP.
To update a version: change `version`, `url` and `sha256` (`gh release view <tag> --json assets` shows digests), then run
`setup --verify-manifest`.

## Not automated

- Running the jailbreak itself, or getting Payload Manager and elfldr onto a console that has neither.
- Changing OnionHEN or ShadowMountPlus settings beyond the title allow-list.
- Verifying the OnionHEN `config.ini` layout on a real console: the editor handles comma or semicolon separated values and warns
  on anything else.
