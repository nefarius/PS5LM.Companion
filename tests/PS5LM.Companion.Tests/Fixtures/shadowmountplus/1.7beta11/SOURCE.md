Excerpt of `/data/shadowmount/debug.log` captured over FTP on 2026-10-25 while ShadowMountPlus 1.7beta7 was installed.
Stored as `install-excerpt.txt` because the repository ignores `*.log`.
It keeps one skipped scan (`source not stable yet`), one fresh install (`installed game PPSA99575`) and a later line that names the title without being an install.

The pin is 1.7beta11. This is not a live capture from that build. The line setup waits on is unchanged:
`log_debug("NOTIFY: installed game %s", ...)` is still at `src/sm_log.c:350` in tag 1.7beta11, and
`source not stable yet` is still logged from `src/sm_scan.c:390`. Checked against the tagged source on 2026-11-05.
