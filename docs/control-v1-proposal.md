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

- **Additive**: `load` and `unload` keep working unchanged; `control/v1` is ignored by old apps and the companion
  detects support by the presence of `control/v1/capabilities.json`.
- **Tiny**: one polling loop the apps already have (about once a second), a JSON parser they already use, no new
  threads or sockets.
- **Safe by construction**: whitelist of settings, validated values, atomic writes, no arbitrary paths or commands.
- **Observable**: every command gets a result file, so the companion never has to guess.

## Layout

Below the state root the apps already use (`/data/PS5LM`, or the Plus sandbox folder):

```
control/v1/
  capabilities.json        written by the app at start
  status.json              written by the app on change (at most once a second)
  commands/<id>.json       written by the companion, consumed (deleted) by the app
  results/<id>.json        written by the app, deleted by the companion after reading
```

`<id>` is a companion-chosen UUID. Files are written to `<name>.tmp` and renamed, so the reader never sees a
partial file. The app ignores `*.tmp`, malformed files (it answers with `invalid`), and anything larger than 64 KiB.

### `capabilities.json`

```json
{ "protocol": 1, "app": "PS5LM", "version": "1.4.0",
  "commands": ["load", "unload", "set_settings", "download_model", "cancel_download"],
  "settings": ["context_size", "gpu_layers", "threads", "temperature"] }
```

### `status.json`

```json
{ "state": "loading", "model": "/data/PS5LM/models/a.gguf", "loadProgress": 0.42,
  "error": null, "download": { "id": "…", "bytes": 123, "total": 456 } }
```

`state` is one of `idle`, `loading`, `ready`, `generating`, `error`. This gives the companion the
"app is running, model loading" state that port 8081 cannot report before it opens.

### Commands

Every command has `id`, `type` and optional `args`:

| `type` | `args` | Effect |
|---|---|---|
| `load` | `{ "path": "<app path>" }` | Same as the legacy `load` file, but acknowledged |
| `unload` | none | Same as the legacy `unload` file |
| `set_settings` | `{ "context_size": 8192, … }` | Validates and applies only whitelisted keys; the app writes `settings.json` itself |
| `download_model` | `{ "url": "https://huggingface.co/…", "sha256": "…", "target": "models" }` | App-native download to `<target>/<name>.part`, rename on success |
| `cancel_download` | `{ "id": "<download command id>" }` | Stops and removes the partial file |

### Results

```json
{ "id": "…", "status": "ok" | "invalid" | "rejected" | "failed", "message": "…" }
```

`invalid` means the command was malformed, `rejected` that the app refused it (unknown path, busy), `failed`
that it was accepted but did not succeed.

## Rules for the app side

1. Poll `control/v1/commands/` in the same loop that polls `load`/`unload`; process commands oldest first.
2. Never act on a path outside the model roots the app already knows; `download_model` accepts HTTPS only and
   only the `target` names `models` or a mounted drive's model folder.
3. Settings: reject unknown keys and out-of-range values, apply atomically, and rewrite `settings.json` from the
   app's own in-memory settings (the app stays the only writer).
4. Delete the command file after writing the result, so a crash replays at most one command and results are
   idempotent per `id`.
5. Keep `status.json` current even while llama-server is down.

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

- Is a once-a-second poll of an extra folder acceptable on the console?
- Which settings are safe to change at runtime, and which need an app restart (the app could report this in `result`)?
- Do the apps want to own the Hugging Face download logic, or keep it out of the payload?
