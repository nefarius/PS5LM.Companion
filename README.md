# PS5LM Companion

Headless web dashboard that manages models and monitors a PS5 running PS5LM or PS5LM Plus, from any browser on your network.

## About

PS5LM and PS5LM Plus only serve their API on port 8081 once a model is on disk and loaded, and their own UI is on the TV.
This companion runs on a PC, NAS or Raspberry Pi and talks to the console over the services those apps already rely on, so it
works before any model exists. It needs no changes to either app.

## Features

- Detects PS5LM (`PPSA99575`) and PS5LM Plus (`PPSA99581`) over FTP.
- Lifecycle-aware status: disconnected, app available without a model, loading, ready, generating.
- Lists models on the console; loads and unloads a model through the apps' command files.
- Downloads GGUF models from Hugging Face on the companion and uploads them to the console.
- Live telemetry from llama-server (port 8081) and ps5-exporter (port 9100) when they are available.

## Support scope

- Companion: Linux, Windows or macOS with .NET 10, or the Docker image (linux/amd64, linux/arm64 via .NET base images).
- Console: jailbroken PS5 running PS5LM or PS5LM Plus with [ftpsrv](https://github.com/ps5-payload-dev/ftpsrv) on port 2121.
- Not supported: launching or killing the app remotely, editing the apps' `settings.json`.

## Quick start

```sh
docker build -f docker/Dockerfile -t ps5lm-companion .
docker run -p 8080:8080 -v ps5lm-companion:/data ps5lm-companion
```

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
