# Console update

`update` brings a console that `setup` already prepared up to the pinned `setup-manifest.json` of this build. It never looks for
newer upstream releases; those arrive by [bumping a pin](setup.md#bumping-a-pin).

```sh
dotnet PS5LM.Companion.App.dll update --host <console> [--apps] [--variant ps5lm|ps5lmplus] [--check] [options]
```

| Option | Purpose |
|---|---|
| `--host` | Console IP or host name (required) |
| `--apps` | Also update the installed PS5LM / PS5LM Plus app |
| `--variant` | With `--apps`: only that variant (an explicit variant that is not installed is a warning) |
| `--restart` | Stop and relaunch an updated payload that is still running (the pinned VoidShell fork, see below) |
| `--check` | Report what is out of date and change nothing |
| `--upgrade-payload-manager` | Upgrade an older Payload Manager without a prompt |
| `--payload-manager-url`, `--ftp-port`, `--manifest`, `--cache` | As for `setup` |

Exit codes: 0 current or updated, 1 bad arguments, 2 something failed, 3 `--check` found updates, 130 cancelled with Ctrl+C.

## What it does

1. Payload Manager must answer on its port (otherwise run `setup`); an older one is upgraded as in `setup`.
2. Each pinned payload's stored file is read back over FTP and compared by SHA-256 with the pinned download. A different file is
   uploaded through Payload Manager into the same slot, so autoload stays valid and is not rewritten. A payload that is not stored
   is reported ("run setup") and not installed.
3. A payload that was not running is launched. One that is running keeps the old build until the next autoload or console
   reboot; this is a warning. Launching it again does not help: the new copy cannot bind the port and the old one keeps serving.
   The stock VoidShell payload is the pinned [nefarius/voidshell](https://github.com/nefarius/voidshell) fork. `--restart` and `--apps`
   both need that fork; websrv is not consulted. With `--restart` a payload that lists a `pidFile` in the manifest (VoidShell: `/data/voidshell/voidshell.pid`) is replaced
   at the end of the run: the pid is read over FTP, must be a running process named `payload.elf` (how elfldr names every payload it
   loads) or the manifest's `<id>.elf` filename (never `pldmgr.elf`, `elfldr.elf`, `mini-syscore.elf`), is stopped through Payload Manager's `/process_kill`, and once the payload no longer answers the new
   build is launched. If it still answers, another copy holds the port (a copy that was launched by hand and failed to start
   also overwrites the pid file), nothing is relaunched and a warning says so; reboot the console then.
4. Extra files (VoidShell `index.html`) are compared by hash and replaced through a `.part` file.
5. With `--apps`: each installed app whose `param.json` differs from the pinned archive is overwritten in place (`param.json`
   last), then the pinned VoidShell fork is asked to rescan. An app that is not installed is never installed. A running app
   (`running_app` from that fork) is skipped with a warning, and so is one whose running state the fork cannot report.

## Dashboard indication

The console status page (and the overview card) flag a component when it is out of sync with this companion's pinned
`setup-manifest.json`. Payload Manager (`/version`) and the pinned VoidShell fork (`SemVer`) are compared on every
status refresh and flagged only when they are older than the pin. A current VoidShell version can still show as out of
sync when its extra file (`index.html`) differs from the pin. Every other payload and extra file is compared by
SHA-256 with the pinned download, the same way `update --check` does: the companion downloads each pin into
`setup-cache` and reads the stored file over FTP. The installed PS5LM / PS5LM Plus app is not hashed as a whole; its
`sce_sys/param.json` is compared with the `param.json` inside the pinned archive.

A missing component is not reported as outdated; the existing "not installed" warnings already cover that. The first
hash pass runs shortly after the console is reachable, then every `PS5:Consoles:{id}:UpdateCheckIntervalMinutes`
(default 360). `0` disables the background pass; live version checks still run. The pass is deferred while the console
is disconnected, loading a model, or receiving a transfer. **Check now** on the status page, or
`POST /api/consoles/{id}/updates/check`, runs it immediately. `GET /api/consoles/{id}/updates` returns the same
`updates` object that is already on `GET /api/consoles/{id}/status`.
