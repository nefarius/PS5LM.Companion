# Load a model when the app starts

Both PS5LM and PS5LM Plus store this on the Settings screen, in `settings.json` under the state root. The companion
reads and writes two keys there. Every other key is left as it is.

| Key | Meaning |
|---|---|
| `auto_load` | `true` asks the app to load `default_model` when it starts. The app's own default is `false`. |
| `default_model` | A model path as the app sees it (`/data/PS5LM/models/a.gguf`, or `/download0/PS5LM/models/a.gguf` in the title's own storage). Empty means no model. |

The app loads that model only when `auto_load` is true, `default_model` is not empty, the file is in the library, and
the model fits in memory. A path that is not in the library is kept and shown as missing; the app then starts without
a model.

The Models page and `GET` / `PUT /api/consoles/{id}/app-settings/auto-load` are the companion's controls. The body is
`{ "autoLoad": true, "defaultModel": "/data/PS5LM/models/a.gguf" }`. `defaultModel` may be `null` to clear it. Turning
auto-load on with no model is refused, because the app would load nothing.

## When it takes effect

The app reads `settings.json` at launch. It does not watch the file. A change applies the next time the app starts.

Saving settings on the console rewrites the whole file from the app's memory. A companion edit made while the app is
running can be overwritten by that save. The Models page says so. The companion still allows the edit: it merges the
two keys into the file that is there, writes `settings.json.tmp` and renames it over `settings.json`, then reads the
file back.

A file that is missing is the app's defaults (`auto_load` false, no model). A file that is not a JSON object, or that
is larger than 64 KiB, is left unchanged. A wrong JSON type on these two keys is reported by the read and replaced by
a write, because the app's parser rejects a type mismatch.

## What still wins at launch

`app-args.txt`, when present, replaces the planner's arguments and names the model. `model.txt` is used once and then
deleted. An unload writes `none` there, so the restart that follows skips auto-load once; the launch after that uses
`default_model` again.

## Versions

The two keys have the same meaning on every version this companion treats as installed:

| App | Versions |
|---|---|
| PS5LM | 0.9.1, 0.10.0 and 0.11.0 |
| PS5LM Plus | 0.7.0 through 0.8.3 |

Which state folder is used is unchanged: PS5LM 0.9.1 and 0.10.0 use `/data/PS5LM` only, PS5LM 0.11.0 and PS5LM Plus
also use the title's own storage, and a missing or unreadable `param.json` checks both and prefers `/data/PS5LM` when
they tie. See [setup.md](setup.md).

Sources: [PS5LM settings](https://github.com/cobanov/PS5LM/blob/v0.11.0/ps5/app/settings.cpp),
[PS5LM launch choice](https://github.com/cobanov/PS5LM/blob/v0.11.0/ps5/app/main.cpp),
[PS5LM Plus settings](https://github.com/abhi245y/PS5LM-Plus/blob/v0.8.3/ps5/app/settings.cpp).
