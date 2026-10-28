Not a live console capture: the release needs hardware. The responses follow the contract of the pinned `v1.1.1-nefarius.3` release (sha256 `fd455f69aade087ba178c9668c94cd05d91b5a904a98071d3d6d6db7d6442a59`), checked two ways:
- `strings voidshell.elf` of the release contains the `/api/stats` format `{"total":%d,...,"session_count":%d,"version":"%s","firmware":"%s"}`, and `VOIDSHELL_VERSION` is `"V1.1"` in the tagged source (`src/types.h`), so `version.json` is unchanged.
- `stats.json` is the `v1.1.1-nefarius.2` capture of a 13.40-era console state with that release's `firmware` field (`"13.40"`, BCD-decoded `kernel_get_fw_version()`) added.
`library.json` is the `v1.1.1-nefarius.2` capture (the library endpoint did not change).
Replace these with a real capture from a console running `v1.1.1-nefarius.3` when one is at hand.
