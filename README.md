# PS5LM Companion

Headless web dashboard that manages models and monitors a PS5 running PS5LM or PS5LM Plus, from any browser on your network.

## About

PS5LM and PS5LM Plus only serve their API on port 8081 once a model is on disk and loaded, and their own UI is on the TV.
This companion runs on a PC, NAS or Raspberry Pi and talks to the console over the services those apps already rely on, so it
works before any model exists. It needs no changes to either app.

## Features

- Manages one or more consoles from one dashboard, each with its own status, models and transfer queue.
- Detects PS5LM (`PPSA99575`) and PS5LM Plus (`PPSA99581`) over FTP.
- Lifecycle-aware status: disconnected, app available without a model, loading, ready, generating.
- Lists models on the console; loads and unloads a model through the apps' command files.
- Downloads GGUF models from Hugging Face on the companion and uploads them to the console.
- Live telemetry from llama-server (port 8081) and ps5-exporter (port 9100) when they are available.
- Shows whether the PS5LM Plus payloads are installed, reads Payload Manager's process list to see whether kstuff-lite, etaHEN (or onionHEN) and ShadowMountPlus are running, records its launches, and checks ftpsrv, elfldr and ps5-exporter directly. Launches an installed payload through [PS5 Payload Manager](https://github.com/itsPLK/ps5-payload-manager).

## Support scope

- Companion: Linux, Windows or macOS with .NET 10, or the Docker image (linux/amd64, linux/arm64 via .NET base images).
- Console: jailbroken PS5 running PS5LM or PS5LM Plus with [ftpsrv](https://github.com/ps5-payload-dev/ftpsrv) on port 2121.
- Payload launch needs [PS5 Payload Manager](https://github.com/itsPLK/ps5-payload-manager) on port 8084 and elfldr already listening on port 9021. Payload Manager cannot start elfldr itself.
- Not supported: launching or killing the PS5LM app remotely, editing the apps' `settings.json`, or detecting a payload that patches the system and leaves no process. Running checks match process names (for example `kstuff.elf`, `etahen.elf`, `onion_daemon.elf`, `shadowmountplus.elf`) from Payload Manager's `/processes_list`.

## Quick start

```sh
docker build -f docker/Dockerfile -t ps5lm-companion .
docker run -p 8080:8080 -v ps5lm-companion:/data \
  -e PS5__Consoles__ps5__Host=192.168.1.50 \
  -e Companion__AccessToken=change-me \
  ps5lm-companion
```

Configuration (environment variables use `__` for nesting). Each console is a section `PS5:Consoles:{id}` where `{id}`
is a stable slug (lowercase letters, digits and hyphens, at most 40 characters); it appears in API routes and keys the
download cache, so do not rename it casually.

| Key | Default | Purpose |
|---|---|---|
| `PS5:Consoles:{id}:Host` | required | Console IP or host name |
| `PS5:Consoles:{id}:DisplayName` | the id | Name shown in the dashboard |
| `PS5:Consoles:{id}:FtpPort` / `LlamaPort` / `ExporterPort` | 2121 / 8081 / 9100 | Service ports |
| `PS5:Consoles:{id}:ElfldrPort` | 9021 | elfldr, required before Payload Manager can launch anything |
| `PS5:Consoles:{id}:PayloadManagerUrl` | `http://{Host}:8084` | Payload Manager base URL when it is not on port 8084 of the console |
| `PS5:Consoles:{id}:Variant` | auto | `PS5LM` or `PS5LMPlus`; required when both apps are installed |
| `PS5:Consoles:{id}:PollIntervalSeconds` | 3 | Status refresh interval |
| `PS5:DefaultConsoleId` | first id alphabetically | Console the dashboard selects first |
| `Companion:AccessToken` | none | Shared secret for the dashboard and `/api` (bearer token or login cookie) |
| `Companion:AllowAnonymous` | false | Allow a network listener without a token (trusted networks only) |
| `Companion:DataDirectory` | `data` | Download cache and transfer list (`/data` in the Docker image; mount a volume) |
| `HuggingFace:Token` | none | Access token, only for gated or private repositories |

The companion refuses to start on a non-loopback address without `Companion:AccessToken`, unless
`Companion:AllowAnonymous` is set. The check covers `urls`/`ASPNETCORE_URLS`, `HTTP_PORTS`, `HTTPS_PORTS` and
`Kestrel:Endpoints`. The token travels in clear text over plain HTTP: put the companion behind a TLS reverse proxy
(the login cookie is marked `Secure` on HTTPS) unless the network is trusted.

## Dashboard

Open `http://<companion>:8080/`. The dashboard is Blazor Server with MudBlazor and updates live over its circuit; it
needs no extra configuration. With more than one console configured, a selector in the top bar chooses the console the
pages show; the choice is per browser tab and every console keeps being polled in the background.

- **Status**: lifecycle, loaded model, generation speed, sensors, drive space, and the payload dependencies. Launch sends an installed payload through Payload Manager. The result confirms the file was transferred. Whether kstuff-lite, etaHEN and ShadowMountPlus are running comes from the process list.
- **Models**: models on the console with Load and Unload.
- **Downloads**: search Hugging Face, queue a file, watch download and upload progress, cancel.

With `Companion:AccessToken` set, the browser is sent to `/login` once and keeps an HttpOnly cookie.

## REST API

All routes live under `/api` and need the token when one is configured (`Authorization: Bearer <token>`).

Console-scoped routes live under `/api/consoles/{consoleId}`; an unknown id returns `404`.

| Route | Purpose |
|---|---|
| `GET /api/consoles` | Configured consoles with their lifecycle; the default is flagged |
| `GET /api/consoles/{consoleId}/status` | Normalised console status, including payload dependencies |
| `GET /api/consoles/{consoleId}/dependencies` | Payload installation and health from the latest status check |
| `POST /api/consoles/{consoleId}/dependencies/{id}/launch` | Launch an installed payload by its stable id (`kstuff-lite`, `etahen`, `shadowmountplus`, `ftpsrv`) |
| `GET /api/consoles/{consoleId}/models` | Models on the console and the storage locations that accept uploads |
| `POST /api/consoles/{consoleId}/models/load` | `{ "path": "<appPath from models>" }`; only known model paths are accepted |
| `POST /api/consoles/{consoleId}/models/unload` | Unload the current model |
| `GET /api/consoles/{consoleId}/transfers` | List that console's transfers |
| `POST /api/consoles/{consoleId}/transfers` | Queue `{ "repo", "fileName", "targetRoot"? }` for that console |
| `DELETE /api/consoles/{consoleId}/transfers/{id}` | Cancel a transfer (`404` if it belongs to another console) |
| `GET /api/hub/search?q=` | Search Hugging Face for GGUF repositories (shared) |
| `GET /api/hub/files?repo=owner/name` | List a repository's `.gguf` files (shared) |

Load and unload write the apps' command file and return `202`; the app picks it up within about a second and
restarts its llama-server. A second command while one is unconsumed returns `409`. If the file is never consumed,
the app is probably not running, which the companion cannot tell apart from "running without a model" while
port 8081 is closed.

Each console has its own transfer queue and runs one transfer at a time; different consoles transfer in parallel. A transfer: download to the cache (resumes after a dropped connection or restart), verify size and
SHA-256, upload as `name.gguf.part`, then rename. The apps never see a half-copied model.

## Upgrading from a single-console setup

This is a breaking change. Replace `PS5__Host` (and the other `PS5__*` keys) with `PS5__Consoles__<id>__Host` and the
matching per-console keys, and change API calls from `/api/status`, `/api/models`, `/api/dependencies` and
`/api/transfers` to the `/api/consoles/{consoleId}/...` routes. Transfers saved by an older version have no console id
and are shown as failed; queue them again. Cached partial downloads from before the change are ignored.

## Build

Prerequisite: .NET SDK 10.0.100 or newer (`global.json`).

```sh
dotnet restore PS5LM.Companion.slnx
dotnet build PS5LM.Companion.slnx -c Release
dotnet test PS5LM.Companion.slnx -c Release
```

## Support policy

The issue tracker is not a help desk. Include the companion version and logs when reporting a bug.

## Licence

MIT, see [LICENSE](LICENSE). Not affiliated with Sony Interactive Entertainment. "PlayStation" and "PS5" are trademarks of
Sony Interactive Entertainment.

## Credits

[cobanov/PS5LM](https://github.com/cobanov/PS5LM), [abhi245y/PS5LM-Plus](https://github.com/abhi245y/PS5LM-Plus),
[ps5-payload-dev](https://github.com/ps5-payload-dev) (ftpsrv, elfldr), [ps5-exporter](https://github.com/Marice/ps5-exporter).
