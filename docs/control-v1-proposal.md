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
  sockets, and no new threads required (downloads advance in small steps from that loop).
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

`<id>` is a companion-chosen UUID. The app requires the file-name id and the JSON `id` to be the same canonical
lowercase UUID (`8-4-4-4-12` hex) and validates it before it builds `results/<id>.json` or any other path from it;
anything else is `invalid`, or deleted and counted in `lastError` when no usable id exists. Files are written to `<name>.tmp` and renamed, so the reader never sees a
partial file; the app ignores `*.tmp`.

**Ordering.** `<seq>` is a zero-padded 12-digit counter that the companion increments per command and also repeats
inside the file as `seq`. The app processes commands in file-name order. A name without a valid `<seq>-` prefix is
processed after all well-formed ones, ordered by modification time and then name. The app rejects (`invalid`,
message `seq mismatch`) a command whose file-name `seq` differs from its JSON `seq`, so renaming a file cannot
change its place in the order.

The companion keeps a durable, monotonic `seq` high-water mark, with or without HMAC, independent of the app's
bounded id journal; it is advanced and flushed before each command file is created and never lowered. If
it ever loses that state, it continues above the highest `seq` it can find in `commands/`, in `results/` and in the
`seq` the app echoes in `status.json` and in every result (`results/<id>.json` carries the command's `seq`, and the
app never lowers the echoed value). Consumed commands and pruned results can leave no trace, so if all of those
sources are gone or unreadable the companion does not guess: it starts a new `seq` epoch at the current UTC time in
milliseconds (always above any earlier counter value, since earlier values grow by one) and a new command never
overwrites an existing file name.

**One total order with the legacy files.** While `control/v1/capabilities.json` is valid, the app treats the legacy
`load` and `unload` files as follows: when it finds one, it first drains every v1 command already present, then
runs the legacy command, so a leftover legacy file cannot jump between two v1 commands. A companion that has
adopted v1 never writes the legacy files.

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

**Protocol gate and trust.** `capabilities.json` is app-owned metadata, but FTP gives every writer the same rights, and
the command HMAC does not authenticate this file. The companion therefore adopts v1 only when `protocol` is exactly
`1` and the file is authenticated: it carries an HMAC `sig` (see Signed app-owned records below) over its
content, or the operator has explicitly confirmed that every FTP writer on this console is trusted (the default
for an unsigned file is to stay on the legacy files). Unknown `protocol` values or an invalid `sig` mean legacy
mode, and the companion re-checks the gate on every start and when the file changes.

The signed `capabilities.json` includes the running app's current `bootId`, and the companion rejects a file whose
`bootId` differs from the one in the fresh `status.json`, so an old signed file cannot be replayed after a restart
(the unsigned, trusted-writer path keeps the documented replay risk). Independently of what is advertised, the app
rejects `download_model` with `rejected` while `remoteDownloads` is off.

**Signed app-owned records.** `capabilities.json`, `processed.json`, `results/<id>.json` and `status.json` all use one
rule. The record is a JSON object that carries `bootId`, `seq` and `sig`. `sig` is the lowercase hex of HMAC-SHA-256,
keyed with the UTF-8 bytes of the secret, over the UTF-8 bytes of `v1-record\n<kind>\n` followed by the RFC 8785
(JCS) canonical JSON of the whole object with `sig` removed. `<kind>` is `capabilities`, `processed`, `result` or
`status`, so a signature for one kind is not valid for another. The companion ignores records that fail the check. On the
trusted-writer path, the effective FTP permissions are the stated risk: any FTP writer can forge them, and the
companion treats results as informational and re-reads the model state from the console rather than trusting a
result alone.

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
| `download_model` | `{ "url": "https://huggingface.co/…", "sha256": "<64 hex>", "target": "models", "fileName": "a.gguf" }` | App-native download to `<target>/<fileName>.part`; verify the SHA-256, then rename (see rules) |
| `cancel_download` | `{ "id": "<download command id>" }` | Stops and removes the partial file |

### Results

```json
{ "id": "…", "seq": 17, "bootId": "…", "status": "ok" | "invalid" | "rejected" | "failed", "message": "…",
  "sig": "<64 hex>" }
```

`invalid` means the command was malformed, `rejected` that the app refused it (unknown path, busy), `failed`
that it was accepted but did not succeed.

## Trust model

The mailbox is a folder on the console's FTP-visible filesystem. ftpsrv accepts any login, so today anyone on the
network can already replace models, write `load`/`unload` and edit `settings.json`; `control/v1` does not change
who can reach the console. What it changes is the impact of a forged command (`set_settings`, `download_model`),
so the riskier commands are gated on the app side instead of trusting the channel:

- `download_model` is off unless the user turns on remote downloads in the app (`remoteDownloads` in
  `capabilities.json` reports the state, default `false`; the app lists `download_model` in `commands` only while
  it is on). `set_settings` only touches the whitelisted keys.
- Optionally the user can set a shared secret in the app and in the companion (`Companion:ControlSecret`), entered
  out of band and never stored under `control/v1/` or in `settings.json`, which are readable over FTP. When a secret
  is set, every command carries `sig` and `issuedAt` (UTC, ISO 8601), and the app answers unsigned, mismatching,
  expired or replayed commands with status `rejected`, message `unauthorized`, without acting.
  - `sig` is the lowercase hex of HMAC-SHA-256 over the UTF-8 bytes of the canonical string
    `v1\n<id>\n<type>\n<seq>\n<issuedAt>\n<canonical args>`, keyed with the UTF-8 bytes of the secret.
  - *Canonical args* is JSON with object keys sorted by ordinal UTF-16 order at every level, no insignificant
    whitespace, strings escaped as in RFC 8785 (only `"`, `\\` and control characters; all other characters are
    emitted as UTF-8), integers without a fraction or exponent, no floats (settings are integers, booleans or
    strings), and absent keys omitted rather than `null`. An empty `args` is `{}`.
  - *Replay protection:* the app accepts only commands whose `issuedAt` is at most 10 minutes old (clocks are
    expected to agree within that window) and whose `seq` is greater than the highest `seq` it has accepted for
    signed commands, which it persists in `processed.json` and never lowers. A copied command therefore stays
    rejected after its id has left the journal.
- Without a secret the console is exactly as exposed as before, which the companion shows in its UI.

## Rules for the app side

1. Poll `control/v1/commands/` in the same loop that polls `load`/`unload`; process commands in `<seq>` order.
   The loop must never block on a download: `download_model` only starts a transfer and returns, and the transfer
   advances in small steps (one chunk per loop pass, or the app's existing background download mechanism) while
   the loop keeps polling, handling `cancel_download` and publishing the status heartbeat. If the app cannot do
   this without a thread, it does not advertise `download_model`.
2. Never act on a path outside the model roots the app already knows. For `download_model`:
   - HTTPS only; `target` names `models` or a mounted drive's model folder.
   - The destination name is `fileName` from the command, never taken from the URL. It must match
     `[A-Za-z0-9][A-Za-z0-9._()+ -]*\.gguf` (no separators, no leading dot), and the app checks that the resolved
     `.part` and final paths stay inside the chosen model folder before writing.
   - The host must be on a fixed allowlist held in the app (default `huggingface.co` and its CDN hosts); the list
     can only be extended in the app, never by a command. Re-check the host on every redirect hop, cap the number
     of redirects, and refuse targets that resolve to loopback, link-local or private address ranges.
   - A download stays active under its id: the app records it in `processed.json` and writes `results/<id>.json`
     only at a terminal state: `ok` after the SHA-256 check and rename, `failed` on digest mismatch, transfer error
     or cancellation. An active download is never started a second time, and a `load` that targets its `.part` or
     final file is rejected with `busy`. After an app restart a `.part` file is discarded, not resumed.
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
   applying the same settings are no-ops). If deleting the command file fails, the app keeps the id in
   `processed.json` for as long as that file exists (it is exempt from the 256-entry eviction) and retries the
   deletion on every pass; it does not process further commands past a command it cannot delete.
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

- If a valid `capabilities.json` exists (see the protocol gate above), the companion switches load/unload to commands (with results), reads `status.json`
  for the lifecycle while 8081 is closed, and enables a Settings page limited to `capabilities.settings`.
- If `download_model` is advertised and `remoteDownloads` is `true`, the Downloads page offers "Download on the console" next to the current
  companion-managed transfer.
- If it does not exist, nothing changes: the legacy files and FTP stay the contract.

## Open questions for the maintainers

- Is a once-a-second poll of an extra folder, plus a 5 second status heartbeat, acceptable on the console?
- Which settings are safe to change at runtime, and which need an app restart (the app could report this in `result`)?
- Do the apps want to own the Hugging Face download logic, or keep it out of the payload?
