# PS5LM Companion

Standalone .NET 10 / ASP.NET Core companion with a MudBlazor dashboard for PS5LM
(`cobanov/PS5LM`) and PS5LM Plus (`abhi245y/PS5LM-Plus`).

- Layout: `src/PS5LM.Companion.{Core,Infrastructure,App}`, `tests/PS5LM.Companion.Tests`.
- `Core` has no third-party dependencies; FluentFTP and Refit types stay in `Infrastructure`.
- Package versions live in `Directory.Packages.props`; warnings are errors.
- Multiple consoles: `PS5:Consoles:{id}` options, one isolated `ConsoleRuntime` each (`IConsoleRegistry`); REST is `/api/consoles/{consoleId}/...`; one transfer queue per console.
- Optional inference gateway (`Inference:Pools`): `InferenceRouter` in `Core` picks a console that reports the pool's model; `App/Inference` forwards `/v1/...` with YARP's `IHttpForwarder`. Requests of one conversation (`X-PS5LM-Session`, `prompt_cache_key`, `metadata.user_id`, or a start-of-conversation hash) prefer the console that served them last, to reuse its prompt cache (`Inference:AffinityMinutes`). Pools may list `AlternateNames` (exact or `prefix*`) resolved by `InferencePoolNames`. YARP stays in `App`.
- The PS5 app only listens on port 8081 once a model is loaded. The network scan confirms a console over FTP first, and through VoidShell on port 7007 when FTP is closed or the login fails. A reverse-DNS name is suggested when it resolves back to the address.
- App check/launch: required pinned `nefarius/voidshell` fork via `IAppControlProvider` in `Core`. HTTP clients in `Infrastructure/Http`; REST `/api/consoles/{id}/apps`.
- `setup` command (`App/Setup`, `Core/Setup`, `Infrastructure/Setup`): one-shot console preparation from the pinned `setup-manifest.json`; see `docs/setup.md`.
- `update` command (`App/Setup/UpdateCommand`, `Core/Setup/UpdateRunner`): compares a prepared console with the pinned manifest and replaces only stale payloads, extra files and (`--apps`) an installed app that is older than the pin or whose version cannot be read; `--check` only reports. A newer `contentVersion` is left in place unless `--force`. `setup` keeps an installed app and points at `update --apps`. Payloads and extra files are compared by hash; the installed app is compared by `sce_sys/param.json` against the archive's `param.json`. The dashboard flags components that differ from the pin (older than the pin for live Payload Manager, VoidShell and app versions; hash or an unversioned `param.json` for the rest) via `ComponentUpdateService`; see `docs/update.md`.
- ftpsrv: the manifest pins the `nefarius/ftpsrv` fork (upstream plus the `accept()` `addr_len` fix, ftpsrv#31). It is `selfReplacing` with `handoffWhileTitleRuns`, so `update --restart` swaps it in place, and `update` uploads and launches it through Payload Manager when FTP is dead; see `docs/setup.md#ftpsrv-fork-build`.
- Payload Manager restart (`Core/Status/PayloadManagerRestartService`, `POST /api/consoles/{id}/payload-manager/restart`): `/shutdown`, else VoidShell `/api/processes` + `/api/kill` (`IProcessControl`), then the pinned ELF through elfldr; see `docs/payload-manager-restart.md`.
- Auto-load at launch (`Core/Models/AutoLoadSettingsService`, `GET`/`PUT /api/consoles/{id}/app-settings/auto-load`): reads and writes `auto_load` and `default_model` in the app's `settings.json`, keeping every other key; see `docs/auto-load.md`.
- Workflow rules: `.cursor/rules/feature-workflow.mdc`. Bumping a supported PS5LM or PS5LM Plus version requires the backwards-compatible layer in `.cursor/rules/app-version-compatibility.mdc`.
