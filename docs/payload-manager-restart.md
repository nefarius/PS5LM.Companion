# Restarting a stuck Payload Manager

Payload Manager can stay alive while its HTTP server is stuck: port 8084 accepts connections, but every request fails
(for example `500` or a dropped connection). The dashboard then shows "Payload Manager is not reachable" with a
**Restart Payload Manager** button. It does the recovery without touching the console.

## What it does

1. Requires elfldr to be listening **and** to keep a connection open for half a second. A wedged elfldr still accepts the
   connection but closes it at once; in that case nothing is stopped, because the old manager cannot be brought back remotely.
2. If the manager's port is open, asks it to exit (`POST /shutdown`, 5 s). A stuck manager never answers.
3. If the port is still open, asks VoidShell for `GET /api/processes` and kills every process whose name starts with
   `pldmgr` (`POST /api/kill`), then waits for the port to close.
4. Fetches the pinned `pldmgr_vX.Y.Z.elf` from the setup manifest (SHA-256 verified, cached in
   `<Companion:DataDirectory>/setup-cache`) and sends it to elfldr.
5. Waits up to 60 s for `/version`, then refreshes the status.

Only one restart runs per console. Payloads that are already running (kstuff, etaHEN, ShadowMountPlus, ...) are not touched, and
autoload is not run again.

## Entry points

- Dashboard button in the Payload Manager warning (disabled while elfldr is not verified).
- `POST /api/consoles/{consoleId}/payload-manager/restart`: `200` when the manager answers again, `503` with a message otherwise,
  `409` while another restart runs.

## Requirements

- elfldr listening on its port.
- For a manager that does not exit by itself: the VoidShell fork release that serves `/api/processes` and `/api/kill`
  (`v1.1.1-nefarius.4` or newer). The reported version stays `V1.1`; an older VoidShell answers `404`, and the restart then says
  to run `update`.

## When a restart will not help

"Payload Manager is running but returned HTTP 500" with the manager's own text means the manager is up and one endpoint
(typically `/list_payloads`) fails, for example on a broken file in its payload folder. Restarting it may not cure that. Read the
text in the warning first.

## Security

None of the console services involved authenticate callers: VoidShell (7007), Payload Manager (8084), elfldr (9021) and ftpsrv
(2121) all trust anyone who can reach them. The `X-VoidShell-Request: 1` header on `POST /api/kill` is only a cross-origin guard
(a web page cannot send it without a preflight that VoidShell never approves). It is not authentication. Keep the console on a
trusted network and do not expose these ports beyond it; the companion's own REST API should be protected the same way.

## Still needs the console

- VoidShell or elfldr is down.
- The port stays open after the kill (something else holds it).
- elfldr stops accepting the new manager after the old one was stopped. Autoload starts Payload Manager again on the next boot.
