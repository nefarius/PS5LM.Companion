websrv was not running on the test console (port 8080 closed), so these shapes come from ps5-payload-dev/websrv source on 2026-10-25.
`/version` is `PAGE_VERSION` in `src/version.h` (`tag`, `date`, `time`, `api`). The tag here is a placeholder.
`/fs/user/app/?fmt=json` is the array built by `dir_read_json` in `src/fs.c`: a `.` entry, then one object per directory with `name`, `mode`, `mtime`, `size`. The two title names are examples.
`/launch?titleId=` answers 503 with an empty body when `sys_launch_title` fails (`launch_request` in `src/websrv.c`).
