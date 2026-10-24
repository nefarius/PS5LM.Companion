# Tuning the app's llama-server arguments

Status: notes for a possible future companion feature. Nothing here is implemented: the companion does not edit
the apps' files, and `docs/control-v1-proposal.md` is the route for any such change. The measurements below come from
one console and one model, so treat them as an illustration, not a benchmark.

## Why this matters

On a PS5 the first reply of a long conversation is slow because of **prompt processing**, not generation or the network.
Coding agents send a large fixed prompt (system prompt plus tool descriptions) with every new conversation:

| Measured (PS5LM Plus, Qwen3.8 27B UD-Q2_K_XL, `-c 32768`, `-ub 64`) | Value |
|---|---|
| Prompt processing | about 97 tokens/s |
| Generation | about 20 tokens/s |
| First request of an agent session (about 6,600 prompt tokens) | about 68 s before the first token |
| Later turns (prefix cached, about 1,600 new tokens) | about 16 s |
| Short chat prompt (about 60 tokens) | about 1 s |

Generation throughput was the same with and without the gateway: 20.7 tokens/s both directly and through it
(only throughput was compared). The Inference page and `POST /api/inference/pools/{alias}/benchmark` show these numbers per console.

## How to see what the app launched

The app writes its arguments to `app.log` in its state root (`ps5lm-app: arg ...`, one per line) and llama.cpp's own timing
to `llama.log` there. The state root is `/data/PS5LM` (PS5LM, and PS5LM Plus with `/data` available) or, on a
sandboxed PS5LM Plus, `/mnt/sandbox/PPSA99581_000/download0/PS5LM` over FTP (`/download0/PS5LM` as the app sees it); see
[real-console-verification.md](real-console-verification.md). Both files can be read over FTP (port 2121). In `llama.log`, `slot print_timing ... prompt eval time`
gives tokens and tokens/s for each request, and `cache_n` in a response's `timings` shows how much of the prompt was reused.

## What the app passes today

PS5LM's planner (`ps5/app/model_plan.cpp` upstream) computes the arguments from the model and the free memory. For a text
model it produces:

```
-m <model> -ngl 999 -fit off -lm none -c <ctx> -fa on -ctk <kv> -ctv <kv>
-b 256 -ub 64 -np 1 -ctxcp 1 -cram 0 --host 0.0.0.0 --port 8081
```

plus the sampling preset for the model family. Models with an image projector or a decision model get `-b 2048 -ub 2048`.
The app also adds `--log-file`, `--metrics` and, when enabled, `--tools`.

The ones that matter for latency:

| Argument | Today | Effect |
|---|---|---|
| `-ub` (micro-batch) | 64 | How many prompt tokens go through the GPU per step. Small values keep the compute buffer tiny but make prompt processing slow. The main lever for first-token time. |
| `-b` (batch) | 256 | Upper bound for `-ub`; must be raised together with it. |
| `-c` (context) | planner's longest that fits | Larger context costs KV-cache memory, which competes with the compute buffer that a larger `-ub` needs. |
| `-ctk` / `-ctv` | `q4_0` or `q8_0` for long contexts | Smaller KV types free memory for a larger `-ub`, at some quality cost. |
| `-np` | 1 | One slot: a second request waits. Raising it divides the context between slots and needs memory. |
| `-ctxcp`, `-cram` | 1, 0 | One prompt checkpoint and no RAM prompt cache. Prefix reuse within the single slot still works (`cache_n`), but a different conversation evicts the previous one. |

## How to override today

`app-args.txt` in the same state root (for example `/data/PS5LM/app-args.txt`), one argument per line, replaces the planner's arguments completely (upstream
`ps5/app/main.cpp`). The file must therefore hold the **full** list from `app.log`, with only the intended values changed
(the app still appends `--log-file`, `--metrics` and `--tools` when they are missing, so the example omits them),
and it is read when the app starts. Delete the file and relaunch the app to return to the planner.

Example: the planner's arguments for the model above with a larger micro-batch.

```
-m
/data/PS5LM/models/Qwen3.8-27B-UD-Q2_K_XL.gguf
-ngl
999
-fit
off
-lm
none
-c
32768
-fa
on
-ctk
q4_0
-ctv
q4_0
-b
512
-ub
512
-np
1
-ctxcp
1
-cram
0
--host
0.0.0.0
--port
8081
--temp
0.6
--top-p
0.95
--top-k
20
--min-p
0
```

## Recommendations

Not measured; try them in this order and compare with the benchmark.

1. **Raise `-ub` and `-b` together** (256, then 512) for the biggest first-token gain. This costs GPU memory. If the model
   already sits near the limit, the load can fail or the console can run out of memory; then lower `-c` (for example
   32768 to 24576) or keep a smaller `-ub`.
2. **Use a smaller or faster model** for interactive use. Prompt and generation speed both scale with model size.
3. **Shrink the client's prompt**: compact-prompt options for local models, fewer enabled tools or MCP servers, and
   avoiding edits near the start of the conversation, which invalidate the cached prefix.
4. **Keep `-np 1`** unless several clients really need to be served at once; the gateway spreads load across consoles
   instead.

## Ideas for a companion feature

- Read `app.log` and `llama.log` over FTP and show the current arguments, prompt and generation speed, and cache reuse
  next to the benchmark.
- Offer an "optimise for prompt speed" preset that proposes `-ub`/`-b` values, with a clear warning about memory.
- Write `app-args.txt` only after showing the full resulting argument list, keep a backup of the previous file, and offer
  a one-click revert. Prefer the `control/v1` settings whitelist over writing this file once upstream supports it.
- Treat a load that fails after an override as a signal to propose reverting.

## Open questions

- The memory cost of each `-ub` step per model size is unmeasured; the upstream planner reserves a fixed amount
  (about 0.75 GiB for text models, 0.5 GiB more for `-ub 2048`) based on calibration on one console.
- Whether upstream would raise the default `-ub` for small models that have memory to spare.
