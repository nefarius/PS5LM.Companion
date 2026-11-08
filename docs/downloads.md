# Whether a Hugging Face model fits

The Downloads page estimates whether a search result can load on the selected console. The estimate is the same one PS5LM's own model page makes (`ps5/app/model_plan.cpp`): the longest context and the best KV-cache type that fit in the free GPU memory, or a line saying how much a 4k context would need. It is an estimate. It does not check whether the pinned llama.cpp build knows the architecture, and downloading stays available either way.

## What the page shows

Search results that include a parameter count get a rough chip, from the weights alone:

| Chip | Meaning |
|---|---|
| likely fits | A 4.5-bit quant, plus 1 GiB of reserve, fits in the free memory |
| low-bit only | Only a 2.7-bit quant (about Q2_K) fits; a 4.5-bit quant does not |
| too big | Even a 2.7-bit quant plus the reserve is larger than the free memory |

A repository with no parameter count has no chip. The chip is not a verdict for a particular file. A switch, off by default, hides repositories whose chip says too big and files whose verdict is Too big, while likely fits, low-bit only, and repositories with no parameter count stay.

Opening a repository checks each top-level model file:

- Weights plus 1 GiB over the budget are **Too big** without reading the file. The text is the one the app uses: the weights alone need more than is free.
- Anything smaller gets a range request for the first 16 MiB. The companion reads the GGUF metadata and runs the planner. A fit shows the context and the cache type, for example `Fits - 64k, q4_0`. The tooltip is the planner's line (`64k context, q4_0 cache: 11.2 of 11.4 GiB`).
- A header that cannot be read, or a packing llama.cpp refuses (PrismML PQ2_0 and PTQ1_0), is **Unknown**.
- A split model (`name-00001-of-00003.gguf`) is judged from the sum of its parts, on the first part, when every part is listed. A missing part is **Unknown** unless the parts that are listed are already too big. The other parts have no verdict.

Two files are checked at a time. A result is remembered by the file name together with its SHA-256, or by the repository and name when the hub has no hash, and by the budget it was planned against. The name stays in the key because a preset can cap the context from the file name.

## Where the budget comes from

The app writes a line to `app.log` when it opens the GPU:

```
device 1 Vulkan0 (AMD Radeon ...): 11.38 / 14.00 GiB free
```

The last line that is not the CPU device is the free memory. `settings.json` supplies `ctx_cap` (default 65536) and `kv_type` (`auto`, `f16`, `q8_0` or `q4_0`). A missing file, a log with no GPU line, a log larger than 8 MiB, or a console that cannot be read, uses `HuggingFace:DefaultBudgetGib` (11.38, the memory PS5LM was calibrated at). The caption under the file list says which one was used.

While the page stays open it rereads the log and the settings every 30 seconds, and replans the open repository when the free memory or those limits change.

PS5LM Plus's planner omits the extra compute-buffer terms, about half a GiB at most for a vision or decision model. The companion uses the PS5LM formula for both apps, which is slightly conservative for Plus.

## Copying a model to other consoles

The Models page and each console's drawer menu can copy `.gguf` files that are already on that console, including vision projectors, to one or more other configured consoles. Inference-pool membership is not required.

- The source file is read once into the companion cache, then checked for size and SHA-256.
- Each destination uses a storage location that accepts uploads. The default is that console's first upload location; a removable drive is used only when it is mounted and selected.
- A destination that already has the same file name is left unchanged. The copy does not overwrite it.
- Two selected files that would write the same name onto the same location are rejected.
- The source console is not a destination of its own copy.
- Each destination upload is an ordinary transfer: `name.gguf.part`, then rename. Cancel one upload from that console's Downloads page, or cancel the whole batch with `DELETE /api/model-copies/{batchId}`.
- A restart continues an unfinished copy. A half-read cache file is discarded and the source is read again. A finished batch drops its cache.
