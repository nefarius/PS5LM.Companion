# Deleting models

PS5LM and PS5LM Plus have no delete action. Their library is a scan of `.gguf` files, and the companion removes a file
by FTP `DELE`. Clearing a finished row on the console's Downloads screen only drops the queue entry; it leaves the GGUF
in place. Cancel deletes a `.part` file, not a finished model.

## Where the files are

Both apps scan these directories when they exist:

| Location | FTP path | App path |
|---|---|---|
| Internal storage | `/data/PS5LM/models` | `/data/PS5LM/models` |
| M.2 | `/mnt/ext0/PS5LM/models` | `/mnt/ext0/PS5LM/models` |
| USB 0 / USB 1 | `/mnt/usb0/PS5LM/models`, `/mnt/usb1/PS5LM/models` | same |
| PS5LM Plus app space | `/mnt/sandbox/PPSA99581_000/download0/PS5LM/models` | `/download0/PS5LM/models` |
| PS5LM Plus app folder | `/data/homebrew/PPSA99581/models` | `/app0/models` |

The app folder is read-only from FTP. The companion lists those files and refuses to delete them. `/data/PS5LM/models` is
shared by both apps when `/data` is available, so a delete there is visible to whichever app scans that folder.

Sources: [PS5LM model directories](https://github.com/cobanov/PS5LM/blob/ecbcb8cc54faf33b7987011b4708d5e0b9ee5f52/ps5/app/main.cpp#L103-L118),
[PS5LM Plus paths](https://github.com/abhi245y/PS5LM-Plus/blob/6729b4acbaf65e23b2e8bc8708b2acba524a7aa8/ps5/app/main.cpp#L110-L135).

## One file per delete

A vision projector such as `mmproj-d1-omni-600M-BF16.gguf` is its own file. The apps hide `mmproj` names from the model
library and, for each ordinary model, pick an `mmproj*.gguf` in the same directory by the longest common file-name
prefix. The selected file is passed to llama.cpp as `--mmproj`.

Sources: [pairing](https://github.com/cobanov/PS5LM/blob/ecbcb8cc54faf33b7987011b4708d5e0b9ee5f52/ps5/app/model_plan.cpp#L103-L144),
[discovery](https://github.com/cobanov/PS5LM/blob/ecbcb8cc54faf33b7987011b4708d5e0b9ee5f52/ps5/app/model_plan.cpp#L401-L427).

The companion deletes the path you select and nothing else.

- Delete `mmproj-d1-omni-600M-BF16.gguf` and leave `d1-omni-600M-BF16.gguf`: the main model remains and loads as text.
  This is the repair for a projector that fails to load while the console UI still offers the pair.
- Delete the main GGUF and leave the projector: the projector stays on disk. The apps do not show it as its own model,
  so remove it in a second delete when you want the space back.
- Several projectors from the same family in one directory can pair ambiguously. Remove the damaged one rather than
  every projector in the folder.

`downloads.json`, `settings.json`, `model.txt` and the `load` / `unload` command files are app state, not per-model
sidecars. Deleting a model does not remove them.

## When deletion is refused

The file stays in place when:

- the console lifecycle is Loading, including a load started on the TV, because the companion may not know which file the app has open
- it is the loaded model, or a load of that model requested from the companion is still inside the load grace window (10 minutes by default)
- any model is loaded, or a companion load is in its grace window, and the selected file is a projector
- a `load` or `unload` command is still waiting on the console
- a transfer of the same file onto the same storage root is queued or running on this console
- the file lives in a read-only root
- the path is not one of the GGUF files the companion just listed

A transfer to another console, a finished transfer, or a different file name does not block the delete. Unload the
model first and wait until the console is back to "no model" before deleting the model that was loaded or its projector.

## The PS5 list updates on its own scan

Neither app watches the disk. The library is built at startup and again after an in-app download or a planner rescan.
An FTP delete can remain on the TV until the app restarts or rescans. The companion's Models page reads FTP, so its
own table drops the file on the next refresh.

Unload and model switches already restart the app, because unloading in place leaks GPU memory
([PS5LM](https://github.com/cobanov/PS5LM/blob/ecbcb8cc54faf33b7987011b4708d5e0b9ee5f52/ps5/app/main.cpp#L1049-L1068),
[PS5LM Plus](https://github.com/abhi245y/PS5LM-Plus/blob/6729b4acbaf65e23b2e8bc8708b2acba524a7aa8/README.md#L142-L149)).
There is no defined result for deleting a GGUF that llama-server still has open. Unload or close the app first.

## Companion

On **Models**, Delete asks for confirmation and names the single file. A projector confirmation says the main model
remains installed. Afterward, restart the PS5 app so its library matches the disk.

`DELETE /api/consoles/{consoleId}/models` with `{ "path": "<appPath>" }` does the same check. `appPath` is the value from
`GET /api/consoles/{consoleId}/models`, for example `/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf`. The call returns
`200` only after a following listing no longer contains the file. `409` means the file is loaded, loading, pending a
command, or being transferred. `400` means the location is read-only or the path is blank. `404` means that path is not
a listed model. `503` means FTP could not be reached or the file was still listed after the delete.

## Manual FTP

When the companion cannot reach the console, the same removal is an FTP `DELE` after the model is unloaded and the app
is closed. [ftpsrv](https://github.com/ps5-payload-dev/ftpsrv/blob/35bae1312b14840c0f95fab93be73d6daaa93bae/cmd.c#L238-L254)
implements `DELE` with `remove()`.

```sh
lftp -p 2121 ftp://<PS5-IP> -e '
rm /data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf
bye'
```

Use the M.2, USB, or PS5LM Plus sandbox path from the table when the file is not on internal storage. Add a second
`rm` for the main GGUF only when that file should go too. Relaunch the app afterwards.
