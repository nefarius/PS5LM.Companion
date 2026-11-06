# Console setup

`setup` prepares a jailbroken console that runs only Payload Manager. Every step can be repeated: what is already there is
skipped.

```sh
dotnet PS5LM.Companion.App.dll setup --host <console> --variant ps5lm|ps5lmplus [options]
dotnet PS5LM.Companion.App.dll setup --verify-manifest
docker run -it --rm --pull always -v ps5lm-companion:/data containinger/ps5lm-companion \
  setup --host <console> --variant ps5lm|ps5lmplus [options]
docker run -it --rm --pull always -v ps5lm-companion:/data containinger/ps5lm-companion \
  setup --verify-manifest
```

The `docker` lines use the published [`containinger/ps5lm-companion`](https://hub.docker.com/r/containinger/ps5lm-companion) image. `-it` keeps the coloured report. `--pull always` fetches the current `:latest` before the command runs. The `dotnet` lines are for a build from source.

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
4. Enable autoload with the payloads in order and a 3 s pause between every step, including entries you already had:
   elfldr, kstuff-lite, OnionHEN, ftpsrv, ShadowMountPlus, VoidShell, PS5 Web File Manager. A longer pause on a payload in
   the manifest is kept. Payload Manager accepts at most 4094 characters. A longer list, pauses included, is left unchanged
   and the report says to shorten the existing entries. `update` does not rewrite this list; re-run `setup` on a console
   that was already prepared.
5. Launch them in that order, skipping any that already run, waiting for each one's port or process. If elfldr does not come up,
   launching stops: Payload Manager sends everything through elfldr and cannot start it.
6. Place extra files over FTP (VoidShell's `index.html` to `/data/voidshell/www/index.html`). Uploaded as `.part`, then renamed.
   Skipped when the size on the console matches (FTP cannot hash).
7. Add the app's title ID to `exact_title_ids` in `[app_jailbreak]` of `/data/OnionHEN/config.ini`, after writing `config.ini.bak`.
   A missing file, section or key, or a bracketed list, is only a warning and nothing is changed.
8. Install the app when it is not already there. A new install is uploaded to `/data/ps5lm-companion/staging/<title>` and renamed
   into `/data/homebrew/<title>`, so ShadowMountPlus never scans a half-copied app. An app that is already installed is left in
   place, including when its `param.json` differs from the pin, and the report points at [`update --apps`](update.md).
   `setup --force` overwrites that app in place with `param.json` last; nothing is deleted, so models in the folder stay.
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
SHA-256 from the release asset. A payload marked `"optional": true` is reported as a warning (with its `optionalNote`) instead of a
failure when it is not up at the end. The stock `voidshell` entry is not optional: setup exits 2 if that fork is not up, and the
companion requires its API at runtime. A payload can list `files` (URL, SHA-256, absolute target under `/data/`) that go over FTP.
To update a version, follow [Bumping a pin](#bumping-a-pin).

## VoidShell fork build

The pinned fork is a required companion dependency. Upstream VoidShell is not a supported substitute for app check, launch,
firmware or `update --apps`.

Upstream VoidShell v1.1.1 imports `sceAppInstUtilAppInstallTitleDir` statically. Firmware such as 13.40 does not export it, so the
loader logs `Unable to resolve 'sceAppInstUtilAppInstallTitleDir'` and the payload exits before `main()` (found with klogsrv, kernel
log on port 3232). The upstream fix ([amilarajans/voidshell#2](https://github.com/amilarajans/voidshell/pull/2)) has had no response,
so the manifest uses the reproducible fork build [nefarius/voidshell `v1.1.1-nefarius.4`](https://github.com/nefarius/voidshell/releases/tag/v1.1.1-nefarius.4)
(Docker build, ps5-payload-sdk v0.45). It also adds `running_app` to `/api/stats`, the running title regardless of focus,
which the companion uses for the app check, and `firmware` (e.g. `13.40`), the console's system software version shown on the overview tiles and
the status page. Verified on FW 13.40 and 10.00.

**Upgrading from `v1.1.1-nefarius.2`.** A normal `setup` rerun skips payloads that are already on the console, so it keeps the old
build and no firmware appears. Run [`update --restart`](update.md) to upload the new `voidshell.elf` and `index.html`, stop the running VoidShell, and launch the uploaded build:

```sh
docker run -it --rm --pull always -v ps5lm-companion:/data containinger/ps5lm-companion \
  update --host <console> --restart
```

`setup --force` uploads those files again and leaves the running copy in place. Reboot the console afterwards so autoload starts the uploaded build.

If the restart reports that another copy still holds the port, reboot the console. The copy that still answers keeps port 7007, and autoload after the reboot starts the uploaded build.

To go back to upstream once it ships the fix: repin `version`, `url`, `sha256` and the `index.html` entry of `voidshell` in the manifest,
then run `setup --verify-manifest`. Leave `voidshell` required. A custom manifest may mark a different payload `"optional": true`
so a failed start is a warning; that does not make the companion work without VoidShell.

## ftpsrv fork build

Upstream ftpsrv v0.21.1 passes an uninitialised `addr_len` to `accept()` in `ftp_data_open()`. Depending on what is on the stack, `accept()`
fails with `EINVAL` and `RETR` answers `550 Invalid argument` (directory listings keep working) until the console is rebooted. The upstream fix
([ps5-payload-dev/ftpsrv#31](https://github.com/ps5-payload-dev/ftpsrv/pull/31)) is not merged yet, so the manifest pins the reproducible fork build
[nefarius/ftpsrv `v0.21.1-nefarius.1`](https://github.com/nefarius/ftpsrv/releases/tag/v0.21.1-nefarius.1): upstream master (v0.21.1 plus the kernel offsets for
firmware 14.00 to 14.22) with that one-line fix, built with ps5-payload-sdk v0.45 in Docker. The MLSD, `RETR` and `STOR` behaviour the companion relies on is unchanged,
so the recorded fixtures under `Fixtures/ftpsrv/0.21.1` still apply. Unlike VoidShell, the fork is not a hard requirement: upstream ftpsrv works wherever the bug does not show.

To fix a console that already runs upstream ftpsrv, run [`update --restart`](update.md). ftpsrv stops any other `ftpsrv.elf` when it starts, so the new copy takes over
without a reboot and without waiting for a running title. If ftpsrv is dead, plain `update` uploads and launches the pinned build through Payload Manager.

To go back to upstream once it ships the fix, repin `version`, `url` and `sha256` of `ftpsrv` and run `setup --verify-manifest`.

## Bumping a pin

1. Change `version`, `url` and `sha256` in `setup-manifest.json` (`gh release view <tag> --json assets` shows digests), and the
   matching `files` entry when the payload ships one.
2. Run `setup --verify-manifest`. It downloads every pinned URL and checks the hash.
3. Refresh the recorded responses under `tests/PS5LM.Companion.Tests/Fixtures/<upstream>/<version>/` and the `SOURCE.md` in that
   folder (upstream version, date, how it was captured). The contract tests run the real clients against those files.
4. Run `setup` against a test console. The contract tests cannot see hardware-only behaviour (the jailbreak, kstuff, the elfldr hand-off).

A weekly workflow (`.github/workflows/upstream-drift.yml`) re-runs `--verify-manifest` and compares each pinned release tag with the
upstream's latest tag. A mismatch opens or updates one "Upstream drift" issue. It is not part of pull-request CI, so a third-party
outage cannot fail a pull request.

## App versions on one companion

A cluster can keep consoles on PS5LM 0.9.1, 0.10.0 and 0.11.0 at the same time. The companion reads each console's
`/data/homebrew/<title>/sce_sys/param.json` and uses its `contentVersion` (`01.009.001` is 0.9.1, `01.011.000` is 0.11.0).

| Installed PS5LM | Where the companion looks for state |
|---|---|
| 0.9.1 or 0.10.0 | `/data/PS5LM` only |
| 0.11.0 or newer | `/data/PS5LM`, and the app's own storage at `/mnt/sandbox/PPSA99575_000/download0/PS5LM` (the app sees `/download0/PS5LM`) when the HEN keeps `/data` closed |
| Missing or unreadable `param.json` | Both of those folders. `/data/PS5LM` wins when both hold state and the `app.log` times are missing or equal |

When 0.11.0 has state in both folders, the one with the newer `app.log` is the one in use. PS5LM Plus is unchanged: it already used its own storage (`PPSA99581`) whenever `/data` was closed, on every version this companion supports.

`setup` does not move a console to the pinned app. `update --apps` replaces an older app, and leaves an app newer than the pin in place unless `--force`. The dashboard flags an app only when it is older than the pin (or its `param.json` differs and the version cannot be read). It does not flag a newer app.

The inference gateway does not route by app version, so a mixed pool keeps working. One difference shows up in the app itself: before 0.11.0, codex against a Qwen model fails with "System message must be at the beginning". 0.11.0 folds those messages on the console. The update flag shows which consoles are behind. Fleet mode is not handled.

## Not automated

- Running the jailbreak itself, or getting Payload Manager and elfldr onto a console that has neither.
- Changing OnionHEN or ShadowMountPlus settings beyond the title allow-list.
- Verifying the OnionHEN `config.ini` layout on a real console: the editor handles comma or semicolon separated values and warns
  on anything else.
