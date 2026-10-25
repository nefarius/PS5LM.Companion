The test console was already on 0.6.3, so these are the 0.5.2 response shapes from itsPLK/ps5-payload-manager tag v0.5.2 (`payload_mgr_list_json` emits a `payloads` array of paths plus `meta`; `GET /get_config` emits `AUTOLOAD_ENABLED` as a JSON boolean; a mutating GET such as `/loadpayload:` is allowed and answers 200).
The paths and process name are examples in that shape, not a live capture.
