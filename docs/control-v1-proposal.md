# Proposal: `control/v1` mailbox for PS5LM and PS5LM Plus

Status: draft for discussion with the upstream projects. Nothing here is implemented, and the companion works
without it (see the README). This document only describes what a small, optional upstream change would add.

## Why

Today the companion drives the apps through two single-purpose files, `load` and `unload`, and infers everything
else (state, progress, errors) from FTP and port 8081. That is enough for load/unload and companion-managed
downloads, but it cannot:

- change settings safely (the apps own `settings.json` and rewrite it; a concurrent write from outside can be lost or corrupt it),
- tell the companion whether a command was accepted, rejected or finished,
- report the app's own state while 8081 is closed (starting, loading, failed to load),
- let the app download a model itself, which would avoid pushing multi-GB files over a slow FTP link.

## Design goals

- **Additive**: `load` and `unload` keep working unchanged; `control/v1` is ignored by old apps. The companion
  adopts it only when `control/v1/capabilities.json` parses, has `protocol == 1` and all required fields
  (`protocol`, `app`, `version`, `commands`); anything else (missing, malformed, another protocol number) means the
  legacy files stay in use.
- **Tiny**: one polling loop the apps already have (about once a second), a JSON parser they already use, no new
  threads or sockets.
- **Safe by construction**: whitelist of settings, validated values, atomic writes, no arbitrary paths or commands,
  and a stated trust model (see below).
- **Observable**: every command gets a result file, so the companion never has to guess.

## Layout

Below the state root the apps already use (`/data/PS5LM`, or the Plus sandbox folder):

```
control/v1/
  capabilities.json        written by the app at start
  status.json              written by the app on change and as a heartbeat
  processed.json           written by the app: ids it has handled, with their results (see delivery)
  commands/<seq>-<id>.json written by the companion, consumed (deleted) by the app
  results/<id>.json        written by the app, deleted by the companion once it has recorded the outcome
```

`<id>` is a companion-chosen UUID. Files are written to `<name>.tmp` and renamed, so the reader never sees a
partial file; the app ignores `*.tmp`.

**Ordering.** `<seq>` is a zero-padded 12-digit counter that the companion increments per command and also repeats
inside the file as `seq`. The app processes commands in file-name order. A name without a valid `<seq>-` prefix is
processed after all well-formed ones, ordered by modification time and then name.

**Malformed and oversized files.** Files over 64 KiB are not read. If the `<id>` can be taken from the file name, the
app writes a `results/<id>.json` with status `invalid` (message `too large` or `malformed`) and deletes the command,
so the companion sees a rejection rather than silence. A file whose name has no usable id is deleted and counted in
`status.json` as `lastError`.

### `capabilities.json`

```json
{ "protocol": 1, "app": "PS5LM", "version": "1.4.0",
  "commands": ["load", "unload", "set_settings", "download_model", "cancel_download"],
  "settings": ["context_size", "gpu_layers", "threads", "temperature"],
  "remoteDownloads": false }
```

### `status.json`

```json
{ "bootId": "…", "seq": 17, "updatedAt": "2026-10-23T20:12:00Z",
  "state": "loading", "model": "/data/PS5LM/models/a.gguf", "loadProgress": 0.42,
  "error": null, "lastError": null, "download": { "id": "…", "bytes": 123, "total": 456 } }
```

`state` is one of `idle`, `loading`, `ready`, `generating`, `error`. This gives the companion the
"app is running, model loading" state that port 8081 cannot report before it opens.

**Freshness.** `bootId` changes on every app start, `seq` increases on every write and `updatedAt` is UTC. The app
rewrites the file at least every 5 seconds while running, even if nothing changed. The companion treats a status
older than 15 seconds, or one whose `bootId` it has not seen after a restart was announced, as stale: the app is not
running, whatever `state` says, and the lifecycle falls back to the legacy FTP inference.

### Commands

Every command has `id`, `type` and optional `args`:

| `type` | `args` | Effect |
|---|---|---|
| `load` | `{ "path": "<app path>" }` | Same as the legacy `load` file, but acknowledged |
| `unload` | none | Same as the legacy `unload` file |
| `set_settings` | `{ "context_size": 8192, … }` | Validates and applies only whitelisted keys; the app writes `settings.json` itself |
| `download_model` | `{ "url": "https://huggingface.co/…", "sha256": "<64 hex>", "target": "models" }` | App-native download to `<target>/<name>.part`; verify the SHA-256, then rename (see rules) |
| `cancel_download` | `{ "id": "<download command id>" }` | Stops and removes the partial file |

### Results

```json
{ "id": "…", "status": "ok" | "invalid" | "rejected" | "failed", "message": "…" }
```

`invalid` means the command was malformed, `rejected` that the app refused it (unknown path, busy), `failed`
that it was accepted but did not succeed.

## Trust model

The mailbox is a folder on the console's FTP-visible filesystem. ftpsrv accepts any login, so today anyone on the
network can already replace models, write `load`/`unload` and edit `settings.json`; `control/v1` does not change
who can reach the console. What it changes is the impact of a forged command (`set_settings`, `download_model`),
so the riskier commands are gated on the app side instead of trusting the channel:

- `download_model` is off unless the user turns on remote downloads in the app (`remoteDownloads` in
  `capabilities.json` reports the state, default `false`). `set_settings` only touches the whitelisted keys.
- Optionally the user can set a shared secret in the app and in the companion (`Companion:ControlSecret`), entered
  out of band and never stored under `control/v1/` or in `settings.json`, which are readable over FTP. When a secret
  is set, every command carries `sig`, an HMAC-SHA-256 of `id`, `type`, `seq` and the canonical `args`, and the app
  answers unsigned or mismatching commands with status `rejected`, message `unauthorized`, without acting.
- Without a secret the console is exactly as exposed as before, which the companion shows in its UI.

## Rules for the app side

1. Poll `control/v1/commands/` in the same loop that polls `load`/`unload`; process commands in `<seq>` order.
2. Never act on a path outside the model roots the app already knows. For `download_model`:
   - HTTPS only; `target` names `models` or a mounted drive's model folder.
   - The host must be on a fixed allowlist held in the app (default `huggingface.co` and its CDN hosts); the list
     can only be extended in the app, never by a command. Re-check the host on every redirect hop, cap the number
     of redirects, and refuse targets that resolve to loopback, link-local or private address ranges.
   - `sha256` is mandatory. The app compares the exact digest of the finished `.part` file and renames it only on a
     match; a missing or mismatching digest deletes the `.part` and yields `failed`.
3. Settings: reject unknown keys and out-of-range values, apply atomically, and rewrite `settings.json` from the
   app's own in-memory settings (the app stays the only writer).
4. Keep `status.json` current (heartbeat) even while llama-server is down.

### Delivery and duplicates

Delivery is at-least-once with idempotent handling, so a crash at any point is safe:

1. Before acting, the app checks `processed.json` (the last 256 ids with their results, rewritten atomically). A
   known id is not executed again; its stored result is written to `results/<id>.json` and the command is deleted.
2. For a new command the app performs it, then records the id and result in `processed.json`, then writes
   `results/<id>.json`, then deletes the command file. A crash between any two steps replays at most the last
   step's work: handlers are idempotent (loading the loaded model, a repeated download of the same file, and
   applying the same settings are no-ops).
3. The companion deletes a result only after it has recorded the outcome durably. The app never deletes results on
   its own; it prunes ones older than 24 hours or beyond the newest 256, so a companion that was down still finds
   them. A companion that restarts re-reads `results/` and ignores ids it already recorded.

## Patch sketch (per upstream project)

Both apps already contain the loop that checks for the `load`/`unload` files. The change is confined to:

1. A `control_v1` module with: `write_capabilities()`, `publish_status()`, `poll_commands()` and one handler per
   command. About 300 lines of code including validation.
2. A call to `poll_commands()` next to the existing mailbox poll, and calls to `publish_status()` where the
   app already changes state (model load start/finish, error, generation start/stop).
3. For `download_model`: reuse the app's existing HTTP code if it has any; otherwise this command is simply left
   out of `capabilities.json` and the companion keeps doing the transfer itself.

The exact file and function names must be confirmed in each repository before opening an upstream issue or pull
request; PS5LM and PS5LM Plus differ in layout, so the patch is expected to be two small, independent changes
rather than a shared one.

## How the companion adopts it

- If `capabilities.json` exists, the companion switches load/unload to commands (with results), reads `status.json`
  for the lifecycle while 8081 is closed, and enables a Settings page limited to `capabilities.settings`.
- If `download_model` is advertised, the Downloads page offers "Download on the console" next to the current
  companion-managed transfer.
- If it does not exist, nothing changes: the legacy files and FTP stay the contract.

## Open questions for the maintainers

- Is a once-a-second poll of an extra folder, plus a 5 second status heartbeat, acceptable on the console?
- Which settings are safe to change at runtime, and which need an app restart (the app could report this in `result`)?
- Do the apps want to own the Hugging Face download logic, or keep it out of the payload?
