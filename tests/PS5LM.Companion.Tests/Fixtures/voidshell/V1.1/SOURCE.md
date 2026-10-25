Captured 2026-10-25 from VoidShell V1.1 (nefarius fork build v1.1.1-nefarius.1) on the test console: `GET /api/version`, `/api/library`, `/api/stats`, `POST /api/rescan`.
`stats.json` has the console IP replaced with `0.0.0.0`. The live `active_game` was `NPXS4003` (8 characters, state `GAME`).
`library-empty.json` is the same library response with `games` cleared.
`stats-prefix.json` keeps that stats shape and sets `active_game` to the first 8 characters of `PPSA99575`, the truncation length observed live, so the companion's prefix match stays pinned to it.
