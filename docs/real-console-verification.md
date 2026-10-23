# Real-console verification

The automated tests use fakes for the console and, for `FtpFileSystem`, a real FTP server (FubarDev.FtpServer).
They cannot prove behaviour against ftpsrv, ps5-exporter or the apps themselves. Run this checklist against a
console before a release, for each of PS5LM and PS5LM Plus, and note the app and ftpsrv versions tested.

## Assumptions taken from the apps' behaviour

- ftpsrv listens on 2121, accepts any login and answers Unix-style `LIST`.
- Each app polls `{stateRoot}/load` and `{stateRoot}/unload` about once a second, reads the first line, deletes the
  file and restarts itself. An empty `unload` file is ignored, so the companion writes `-`.
- State root: `/data/PS5LM` for both apps; PS5LM Plus may use `/mnt/sandbox/PPSA99581_000/download0/PS5LM`, which
  the app sees as `/download0/PS5LM`.
- llama-server (8081) only listens once a model is loaded; ps5-exporter listens on 9100.

## Checklist

Start the companion with `PS5__Host=<console>` and open the dashboard.

1. **No model, app not started**: status shows the console and variant after the app has run once; Models lists the
   files on the console.
2. **Load**: press Load. The command file disappears within a few seconds, the status reads *Loading model* for the
   whole restart (minutes for large models), then *Ready* with the model name once 8081 answers.
3. **Command not consumed**: stop the app, press Load. The dashboard warns that the command is waiting and does not
   claim the model is loading forever.
4. **Unload**: press Unload. The app restarts without a model and the status returns to *No model loaded*.
5. **Download**: queue a small GGUF. Watch download then upload progress; the file appears under `models/` only
   when complete (never as `.part` in the model list); the Models page shows it afterwards.
6. **Interrupted upload**: pull the network cable or sleep the console mid-upload. The transfer fails, no partial
   `.gguf` remains, and queuing the file again reuses the verified download from the cache.
7. **Storage**: with ps5-exporter running, free space shows on the dashboard and a download larger than the free
   space is refused before downloading. Without it, uploads are attempted.
8. **Plus sandbox**: on a sandboxed PS5LM Plus, loading a model from `/app0/models` works and uploads land in the
   sandbox folder.
9. **Both installed**: with both apps present and `PS5:Variant` unset, the dashboard explains how to choose.

Record any difference (especially in ftpsrv listings, rename behaviour and command-file timing) as a test fixture.
