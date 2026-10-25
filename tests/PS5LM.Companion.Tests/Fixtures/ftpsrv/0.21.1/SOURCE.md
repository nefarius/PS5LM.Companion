Raw MLSD bodies from ftpsrv v0.21.1 on the test console, 2026-10-25 (banner `Version: v0.21.1 (built Aug 20 2026)`).
`mlsd-shadowmount.txt` is `CWD /data/shadowmount` then `MLSD` with no path.
`mlsd-empty.txt` is `CWD /data/OnionHEN/games` then `MLSD`: only `.` and `..`.
`mlsd-ignored-path.txt` is `CWD /` then `MLSD /data/shadowmount`: ftpsrv ignores the path and lists the working directory (the root). Control replies observed alongside these: `150 Opening data transfer`, `226 Transfer complete`, and `226 File deleted` for `DELE`.
