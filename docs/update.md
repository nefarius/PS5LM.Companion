# Console update

`update` brings a console that `setup` already prepared up to the pinned `setup-manifest.json` of this build. It never looks for
newer upstream releases; those arrive by [bumping a pin](setup.md#bumping-a-pin).

```sh
dotnet PS5LM.Companion.App.dll update (--host <console>)... | --all-consoles [--apps] [--settings] [--variant ps5lm|ps5lmplus] [--check] [--force] [options]
docker run -it --rm --pull always -v ps5lm-companion:/data containinger/ps5lm-companion \
  update --host <console> [--apps] [--settings] [--variant ps5lm|ps5lmplus] [--check] [--force] [--restart] [options]
```

The `docker` line uses the published [`containinger/ps5lm-companion`](https://hub.docker.com/r/containinger/ps5lm-companion) image. `-it` keeps the coloured report. `--pull always` fetches the current `:latest` before the command runs, so the pins compared against the console are the ones in the current release. Mount `/data` so downloads are cached. The `dotnet` line is for a build from source.

| Option | Purpose |
|---|---|
| `--host` | Console IP or host name. Repeat the option to visit more than one console |
| `--all-consoles` | Every console in `PS5:Consoles` and in `{Companion__DataDirectory}/consoles.json`. A stored console whose id is already configured is skipped. Cannot be combined with `--host` |
| `--apps` | Also update the installed PS5LM / PS5LM Plus app |
| `--settings` | Rewrite the autoload list, the Payload Manager countdown and OnionHEN's bundled-kstuff switch. With `--check`, only report them |
| `--variant` | With `--apps`: only that variant (an explicit variant that is not installed is a warning) |
| `--restart` | Stop and relaunch an updated payload that is still running (VoidShell by its pid file, ShadowMountPlus by launching the new copy; see below) |
| `--check` | Report what is out of date and change nothing |
| `--force` | With `--apps`: also replace an installed app that is newer than the pin |
| `--upgrade-payload-manager` | Upgrade an older Payload Manager without a prompt |
| `--payload-manager-url`, `--ftp-port`, `--manifest`, `--cache` | As for `setup` |

Exit codes: 0 current or updated, 1 bad arguments, 2 something failed, 3 `--check` found updates, 130 cancelled with Ctrl+C. A run over several consoles prints a one-line summary per console and returns the worst of those codes (2, then 1, then 3, then 0).

## What it does

1. Payload Manager must answer on its port (otherwise run `setup`); an older one is upgraded as in `setup`.
2. Each pinned payload's stored file is read back over FTP and compared by SHA-256 with the pinned download. A different file is
   uploaded through Payload Manager into the same slot, so autoload stays valid. The list itself is rewritten only with
   `--settings`. A payload that is not stored
   is reported ("run setup") and not installed.
3. A payload that was not running is launched. One that is running keeps the old build until the next autoload or console
   reboot; this is a warning. Launching it again does not help when the new copy cannot bind the port the old one holds.
   A payload with neither a `pidFile` nor `selfReplacing` says that `update --restart` cannot replace it while it runs.
   The stock VoidShell payload is the pinned [nefarius/voidshell](https://github.com/nefarius/voidshell) fork. `--restart` and `--apps`
   both need that fork. With `--restart` a payload that lists a `pidFile` in the manifest (VoidShell: `/data/voidshell/voidshell.pid`) is replaced
   at the end of the run: the pid is read over FTP, must be a running process named `payload.elf` (how elfldr names every payload it
   loads) or the manifest's `<id>.elf` filename (never `pldmgr.elf`, `elfldr.elf`, `mini-syscore.elf`), is stopped through Payload Manager's `/process_kill`, and once the payload no longer answers the new
   build is launched. If it still answers, another copy holds the port (a copy that was launched by hand and failed to start
   also overwrites the pid file), nothing is relaunched and a warning says so; reboot the console then.
   ShadowMountPlus sets `selfReplacing`: a new copy finds the running `shadowmountplus.elf`, signals it, and takes over (it waits up to 60 seconds).
   `--restart` launches that new copy directly and waits until the old process ids are gone and a new one with the same name exists.
   It does not launch while a title is running, or when VoidShell cannot say whether one is: the handoff unmounts titles ShadowMountPlus mounted.
   If the old process is still there, or no new process appears, the old build keeps running and a warning says the new one loads at the next autoload or reboot.
   ftpsrv sets `selfReplacing` and `handoffWhileTitleRuns` (the [nefarius/ftpsrv](https://github.com/nefarius/ftpsrv) build kills any other `ftpsrv.elf` when it starts, and a handoff only drops FTP briefly), so `--restart` replaces it even while a title runs or when VoidShell cannot say. That handoff reports success only once the new copy answers on the FTP port; if the process takes over but the port does not, the warning names the port.
   When ftpsrv does not answer at all, nothing can be compared over FTP: `update` uploads the pinned ftpsrv through Payload Manager without comparing, launches it, and then continues. The same happens once when ftpsrv answers but a stored file listed in its folder cannot be downloaded (the `550` bug): the pinned build is uploaded (over FTP through a `.part` rename if Payload Manager refuses a file in use) and handed over by name. If neither works the run says a hung ftpsrv needs a reboot. `--check` only reports.
4. Extra files (VoidShell `index.html`) are compared by hash and replaced through a `.part` file.
5. With `--apps`: each installed app whose `param.json` differs from the pinned archive is overwritten in place (`param.json`
   last), then the pinned VoidShell fork is asked to rescan. An app whose `contentVersion` is newer than the pin is left in
   place (a warning, not an update) unless `--force`. An app that is not installed is never installed. A running app
   (`running_app` from that fork) is skipped with a warning, and so is one whose running state the fork cannot report.

## Boot settings

`--settings` brings the boot configuration in line with what `setup` would write. It does not restart a payload. The new list,
countdown and bundled-kstuff switch take effect at the next autoload or console boot. `--check --settings` reports the same
differences and changes nothing (exit code 3 when it finds any). Without `--settings`, `update` leaves the list and the countdown alone.

A console that has none of the pinned payloads stored is left alone; run `setup` for that one. Entries already in the autoload list
that are not pinned payloads are kept. The check flags only the pinned entries:

- Their order differs from the manifest, or a stored pinned payload is missing from the list.
- The pause after one of them is shorter than its manifest pause, or shorter than 3 s.
- A payload id is listed twice, or more than one kstuff is listed. `--settings` does not save that list; remove the extra entry and run it again.
- Autoload is off.
- `AUTOLOAD_DELAY` is reported and is under 10 s. A manager that does not report the countdown is not flagged.
- OnionHEN's `[kstuff] autoload` is not `false` while kstuff-lite is stored. A console that does not have it stored is not flagged, and its bundled kstuff is left as it is.
- kstuff-lite is in the autoload list while OnionHEN is stored and `/data/OnionHEN/config.ini` does not exist. That is drift: the next boot would load two kstuffs. `--settings` removes the kstuff-lite entry. A list that already omits it is not drift; the missing file stays a warning and boot settings stay pending until OnionHEN creates the file. A console with no stored OnionHEN keeps kstuff-lite in the list, because nothing there loads a bundled copy. Once the file exists, the recommendation is kstuff-lite in the list and the bundled copy off, and the same `--settings` run writes the switch before the list.

`update` will not launch kstuff-lite or OnionHEN when that send would put two kstuffs on the kernel. It reads the process list and the config again immediately before the send. Replacing ftpsrv (`--restart`) happens after `--settings`, so the config read is not racing an ftpsrv handoff.

Payload Manager older than 0.6.0 does not store `AUTOLOAD_DELAY` (`get_config` arrived in 0.6.0). Those consoles are not flagged
and the list is not written. An OnionHEN slot that names a build older than 0.9.0 (`repo/onionhen@v0.0.13`) does not read
`[kstuff] autoload`, so that switch is not flagged and not written. A `local/onionhen` slot has no revision and is treated as
current. FTP not answering is reported as not confirmed and is not drift; a kstuff-lite entry that is already in the list is left there.

The background file check records these findings on `updates.settings` next to the outdated payloads. `POST /api/consoles/{id}/updates/check`
refreshes both.

## Dashboard indication

The console status page (and the overview card) flag a component when it is out of sync with this companion's pinned
`setup-manifest.json`. Payload Manager (`/version`) and the pinned VoidShell fork (`SemVer`) are compared on every
status refresh and flagged only when they are older than the pin. A current VoidShell version can still show as out of
sync when its extra file (`index.html`) differs from the pin. Every other payload and extra file is compared by
SHA-256 with the pinned download, the same way `update --check` does: the companion downloads each pin into
`setup-cache` and reads the stored file over FTP. The installed PS5LM / PS5LM Plus app is not hashed as a whole; its
`sce_sys/param.json` is compared with the `param.json` inside the pinned archive. A `contentVersion` newer than the pin is
not flagged. An older app, or one whose version cannot be read, is flagged when `param.json` differs.

The same page shows a separate warning when `updates.settings` is not empty: boot settings differ from the recommendation,
one line per finding, and the `update --host ... --settings` command. Warnings that are not drift, such as a duplicate
list that was not saved or an OnionHEN config that is missing, are published on `updates.warnings` and shown beside that.
A missing config stays "not confirmed", not a successful fix. **Fix settings** asks for confirmation, then
`POST /api/consoles/{id}/boot-settings/apply` rewrites only that configuration. It takes effect at the next boot or
autoload and does not restart a payload. The request answers 409 while a transfer or a model load is running, and 503
when the pinned manifest is missing. The overview card adds "Boot settings out of date" beside "Updates available".

A missing component is not reported as outdated; the existing "not installed" warnings already cover that. The first
hash pass runs shortly after the console is reachable, then every `PS5:Consoles:{id}:UpdateCheckIntervalMinutes`
(default 360). `0` disables the background pass; live version checks still run. The pass is deferred while the console
is disconnected, loading a model, or receiving a transfer. **Check now** on the status page, or
`POST /api/consoles/{id}/updates/check`, runs it immediately. `GET /api/consoles/{id}/updates` returns the same
`updates` object that is already on `GET /api/consoles/{id}/status`.
