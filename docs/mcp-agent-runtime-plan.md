# MCP agent runtime implementation plan

Status: design proposal; not implemented. Last reviewed: 2026-10-26.

A durable MCP service that lets Cursor, Codex, and ChatGPT delegate bounded coding work to locally hosted models.

**Recommendation:** use PS5LM Companion as the control plane. Add a durable job coordinator, a model-aware scheduler, and dedicated sandboxed execution workers. Existing llama-server backends and optional native Ollama endpoints provide inference; the runtime provides the agent loop, tools, state, and execution policy.

**Assumptions.** Initial users are trusted team members working on Git repositories. Endpoints are registered administratively. First delivery produces reviews and patch proposals without automatic push or merge. The design is adapted to PS5LM Companion's .NET 10 / ASP.NET Core stack; section 17 records the repository-specific integration decisions. This is a proposed design, not an implemented or benchmarked system.

## 1. Product contract

A developer asks the main assistant to investigate a defect or implement a bounded change. The assistant submits an objective, source snapshot, acceptance criteria, and capability profile. The service provisions an isolated workspace, selects a qualified model, runs an agent loop, validates permitted changes, and returns a report plus artifacts. The parent assistant assesses and integrates the result.

This is a delegated agent behind an MCP tool. It does not automatically inherit the host's native sub-agent lifecycle, full conversation, local disk, tools, or credentials. The web service owns its jobs independently of a chat connection.

| Profile | Capabilities | Result |
| --- | --- | --- |
| Review | Read, search, inspect diffs; no execution of repository code. | Findings with verified source references. |
| Investigate | Read plus named build/test recipes in a sandbox. | Reproduction evidence and proposed remedy. |
| Implement | Edit a disposable copy and run authorized recipes. | Patch, change summary, validation manifest. |
| Integration, later | Apply an approved result through a separately authorized path. | Local changes or a branch/PR if enabled. |

Runtime completion, test success, and integration success are separate outcomes. A completed investigation may correctly report failing tests or insufficient evidence. No model-generated claim substitutes for recorded execution evidence.

## 2. Architecture

| Component | Responsibility | Boundary |
| --- | --- | --- |
| MCP adapter and web API | Authentication, schemas, discovery, job submission, status and results. | Existing web application; no arbitrary code execution. |
| Coordinator | Queue, state machine, quotas, leases, cancellation, policy and recovery. | Trusted application services and background workers. |
| Model registry and scheduler | Inventory, qualification, inference-slot reservations and routing. | Trusted control plane. |
| Agent worker | Conversation loop, context assembly, tool dispatch and checkpoints. | Separate process or host. |
| Sandbox executor | Job-specific files and subprocesses under resource/network limits. | Per-job container or VM. |
| Database and artifacts | Durable jobs, event ledger, source snapshots, logs and patches. | Private durable storage. |
| Optional local companion | Export dirty IDE state and review/apply results locally. | Developer workstation with narrow credentials. |

**Request path:** host assistant → MCP → durable job → agent worker → scheduler → Ollama. Tool execution follows a separate worker → policy → sandbox path. GPU servers need not mount repositories; execution workers need not have GPUs.

Start with a modular control plane plus separately deployed workers. Add SQLite-backed job persistence on a local persistent volume for a single coordinator; the existing application uses JSON stores rather than a durable job database. PostgreSQL-backed transactional claiming and leases are a later option for multiple coordinators. Introduce a broker or workflow engine only when operational needs justify it. MCP, REST and the UI should call the same authorized application services.

For ASP.NET Core, evaluate the official C# MCP SDK [6], typed HttpClient for Ollama, your existing persistence layer, BackgroundService workers, and OpenTelemetry. Keep protocol handling out of the core runtime. Pin versions after a client-compatibility spike. Linux workers suit many web projects; Windows-only toolchains require a dedicated Windows execution pool.

## 3. Source access and patch integration

**Solve source transfer first.** A web-hosted MCP service does not gain access to Cursor's checkout merely because Cursor calls it. MCP roots describe boundaries; they do not mount a remote filesystem.

| Mode | Implementation | Recommendation |
| --- | --- | --- |
| Registered repository and commit | Repository ID plus exact commit. Trusted importer fetches and creates a sanitized immutable tree. | First release: simplest reproducible source. |
| Uploaded snapshot | Companion exports a canonical tree including selected local edits/new files. | Add when dirty IDE state is required. |
| Local companion execution | Service coordinates an authorized local worker using isolated copies. | Later, for local-only dependencies/toolchains. |

Never accept an arbitrary clone URL or absolute server path from model arguments. Resolve registered IDs through access checks. Fetch credentials must not enter the execution sandbox. Disable checkout hooks, custom filters, submodule fetches, LFS downloads, and package scripts unless separately authorized.

Uploaded trees need traversal, absolute-path, link, expansion-size and file-count checks. Use an inclusion manifest; .gitignore alone is not secret filtering. Exclude credential stores and .env files by default. The companion should disclose selected export paths before its first upload.

Assign every tree a snapshot ID, canonical content digest, path/hash manifest, and base commit where applicable. Give every attempt a separate writable copy. Git worktrees may optimize trusted storage but are not a security boundary; do not expose shared Git metadata to untrusted execution.

### Patch handoff

1. Create a manifest of changed, new, deleted, renamed, binary and mode-modified paths.
2. Produce a binary-capable Git patch where supported, plus a bounded readable summary.
3. Include base snapshot digest, output hashes, validation records and toolchain identity.
4. Use a companion or host filesystem tool to check the current local source matches the expected base before applying. A successful patch check alone does not establish source identity.
5. On drift, report conflicts or create a new attempt against the updated snapshot. Never overwrite current user edits.

A text-only MCP host may need a download or companion for integration. Explicitly test that workflow rather than promising every client can automatically apply remote artifacts.

## 4. MCP contract

Expose authenticated Streamable HTTP from the web project. Pin SDK and protocol versions, then test exact installed clients. Current MCP transport and Tasks behavior differs from earlier revisions; do not assume the newest spec is implemented everywhere [4], [5].

| Tool | Purpose | Constraints |
| --- | --- | --- |
| `agent_capabilities` | List accessible repositories, profiles, model pools and limits. | No other tenants' inventory or infrastructure addresses. |
| `agent_start` | Durably enqueue and return a handle quickly. | Idempotency key, immutable source, server-clamped budgets. |
| `agent_status` | State plus events after a cursor. | Bounded pagination; recommended polling interval. |
| `agent_result` | Compact final report and artifact references. | Explicit completeness, stop reason and validation state. |
| `agent_artifact` | Bounded text ranges or authorized download. | Artifact IDs, never arbitrary paths/URLs. |
| `agent_cancel` | Persist cancellation intent and initiate cleanup. | Idempotent; accurately report remaining work. |

These are ordinary tools, not protocol methods. This baseline works without native MCP Tasks. Add a version-specific Tasks adapter only when the client advertises matching support. Map it to the same job store; do not maintain two execution lifecycles. Never mix an older task shape with the newer extension [5].

Return readable text plus matching structured content/output schemas where supported. Essential results must remain accessible without resource or UI-extension support. Avoid sending enormous patches into the parent conversation. Short-lived download URLs can grant bearer access and require equivalent tenant protection.

### Illustrative start arguments

```json
{
  "idempotency_key": "caller-generated-unique-key",
  "objective": "Fix duplicate submissions in the checkout form",
  "source": { "snapshot_id": "snap_example" },
  "profile": "implement",
  "model_pool": "coding-qualified",
  "acceptance_criteria": [
    "Repeated submit cannot send duplicate requests",
    "Existing checkout tests still pass",
    "Add a regression test for the reproduced case"
  ],
  "constraints": {
    "editable_paths": ["src/checkout/", "tests/checkout/"],
    "validation_recipes": ["checkout-tests"],
    "network_profile": "offline-build"
  },
  "requested_limits": {
    "wall_seconds": 900,
    "model_turns": 30,
    "tool_calls": 100,
    "generated_tokens_total": 20000
  }
}
```

Every requested capability must be authorized. Effective limits are the stricter of user request and server policy. Objective text cannot grant access. Validate source ownership, recipe IDs, model pools and edit scopes before admission.

Initially omit approval-granting tools from MCP. If a job needs more privilege, pause it and direct the operator to an authenticated web approval screen. Bind approval to job, action digest, workspace revision, scope and expiry. An assistant must not approve its own escalation. Approval of an outer MCP call is not blanket authorization for arbitrary nested commands.

Document to host agents how to choose a task, provide criteria, poll without busy waiting, inspect evidence and retrieve artifacts. A returned handle does not guarantee the host will keep polling after a chat turn ends; show durable jobs in the web UI regardless.

## 5. Durable jobs, recovery and cancellation

Use an explicit lifecycle: `queued → provisioning → running → validating → finalizing → completed`. Active work can enter `awaiting_approval`, `recovering`, `cancel_requested` or `failed`. Reach `cancelled` only when the controller verifies execution stopped. Terminal states are immutable; retries create new attempts or jobs.

- **Submission:** persist job and queue/outbox entry atomically before acknowledgment. Scope idempotency keys by tenant/caller and compare the request digest on reuse.
- **Ownership:** one attempt holds a renewable lease and increasing fencing token. Executor operations verify ownership. Database fencing alone does not stop a stale running subprocess.
- **Recovery:** terminate or fence the old sandbox at the execution boundary before takeover. Otherwise pause for intervention rather than creating competing writers.
- **Journal:** persist complete model responses before dispatch, tool intent before execution, and result afterward. Checkpoints include conversation, source/output revision and cumulative budgets.
- **Ambiguous mutation:** do not replay a command interrupted between execution and result recording. Reconcile by operation ID, or discard the sandbox and restart from a known immutable checkpoint/source.
- **Cancellation:** stop new inference, abort active HTTP, request graceful process shutdown, then kill the execution boundary after a configured grace period. Verify cleanup and reap orphans.
- **Inference uncertainty:** client cancellation is not proof that an Ollama node immediately stopped computing. Keep potentially outstanding work accounted for until reconciled.
- **Disconnect:** losing an MCP/HTTP connection does not cancel an accepted durable job. Explicit cancellation and job TTL determine lifetime.

Track execution and cleanup separately, for example `execution=cancelled; cleanup=pending`. Use distinct queue deadline, running wall budget, approval expiry, artifact retention and workspace TTL. Waiting for approval should release inference reservations.

Exactly-once arbitrary external effects are not a general guarantee. Early profiles should confine effects to disposable workspaces. Network-dependent publishing and integration need separately designed idempotency and authorization.

## 6. The agent execution loop

Ollama supports tool definitions and iterative tool-result conversations [1]. The model proposes actions; deterministic service code validates and executes them.

```text
claim attempt; provision sandbox; load source, grant and budget
while active:
    check lease, cancellation, deadlines and cumulative limits
    assemble context within the qualified model's usable window
    reserve an eligible inference slot
    call Ollama; persist the complete assistant response
    release or reconcile the inference reservation
    if tool calls exist:
        validate tool names, schemas, permissions and preconditions
        journal intent; execute allowed calls; persist bounded results
        checkpoint conversation and workspace revision
        continue
    validate the proposed final result
    run server-selected acceptance recipes
    if repair is authorized and budget remains:
        append actual validation evidence; continue
    finalize report, patch, validation manifest and cleanup
    return
on any limit: return an explicit partial result and stop reason
```

Assemble streamed calls completely before executing. Preserve assistant tool-call messages and matching results in the provider-supported format. Never parse commands out of prose/code fences/private reasoning. Keep provider-required internal fields if needed, but do not present chain-of-thought as progress.

Serialize mutations and process execution initially. Parallel reads must observe a consistent revision. Multiple calls in one response still need deterministic dispatch and individual authorization. A denied call returns structured feedback; it never silently upgrades the grant.

Allow one bounded schema repair, then stop or use a prequalified fallback. Detect repeated call/result signatures and unchanged-revision loops. Limit repair rounds. Charge all reviewer and child work to the root budget. Recover counters from the ledger after a crash.

Finalization independently derives test outcomes from executor records, checks patch scope and hashes, and distinguishes observed facts from the model's assessment.

## 7. Local tools

| Tool | Arguments | Enforcement |
| --- | --- | --- |
| `list_files` | Relative path, glob, cursor and limit. | Root confinement; secret/generated-file exclusion. |
| `search_text` | Pattern, scope, literal/regex mode. | Time/output limits; no shell interpolation. |
| `read_file` | Relative path and bounded range. | Binary detection, provenance and truncation markers. |
| `apply_patch` | Structured changes plus expected input hashes. | Editable scopes, staged validation, drift rejection. |
| `run_recipe` | Registered ID and schema-validated parameters. | Known executable/argv, cwd, environment and limits. |
| `git_diff` | Bounded relative scope. | Read only; account for newly created files. |
| `validation_results` | Invocation ID and output cursor. | Immutable execution observations. |

Recipes such as frontend-unit-tests make behavior auditable. They do not make execution safe: builds/tests, npm lifecycle scripts and MSBuild targets can execute repository-controlled code. The sandbox is the containment boundary. Prepare dependencies under a separate locked, controlled-egress recipe.

Add a general process tool only in a stronger profile later. Prefer executable and argument vectors over shell strings, but even an allowed executable may run arbitrary code. A shell-command allowlist is not isolation.

Record exit status, timeout/cancellation reason, duration, cwd, toolchain, output bytes, truncation and invocation ID. Store bounded full logs privately, return excerpts. Terminate descendants through the supervisor. Cap CPU, RAM, process count, disk and wall time independently of model behavior.

## 8. Ollama cluster inventory and scheduler

### Inventory and qualification

Poll administratively registered endpoints. Use `/api/tags` for available model digests/details and `/api/ps` for loaded-model observations [2], [3]. Record Ollama version, model digest, quantization, configured context, qualification results and measured latency. Tags alone are not immutable identities.

For the existing PS5 backends, start with an OpenAI-compatible chat adapter behind a provider-neutral interface and preserve shared gateway admission. For additional native Ollama endpoints, use `/api/chat`, which supplies tools, runtime options, formats and metrics [7]. This MCP design does not require Ollama to implement the Responses API.

Qualify each server/model configuration with tool-call fixtures, follow-up tool use, JSON argument validity, source-reference accuracy, patch tasks, context tests, honest validation reporting and cancellation measurements. Metadata is a hint; a passed fixture is stronger evidence. A weak tool-calling model may still perform bounded text tasks; do not promote it to autonomous coding by scraping prose.

### Admission and placement

Filter by authorization, qualification, model identity, context needs, node health, deadline feasibility and reservation capacity. Rank eligible nodes by estimated finish time:

```text
estimated finish = already reserved work
                 + measured cold-load penalty
                 + estimated prefill time
                 + estimated output time
                 + uncertainty / unhealthy-node penalty
```

Favor a suitable resident model and beneficial node affinity. Do not assume KV caches transfer across nodes. Migration replays a durable transcript or explicit checkpoint summary and records the model/node change.

Reserve inference slots atomically across all service replicas. Count actual inference requests, including reviewers, rather than jobs. Start with one slot per node/deployment and increase from benchmarks. External callers bypass reservations: dedicate nodes or account conservatively for observed external load.

Ollama's context, residency and concurrency controls affect memory and queueing [8]. Calibrate to the installed version. Loaded-model observations do not establish actual free memory or safe parallelism; attach host telemetry if required and label unknowns explicitly. Use keep-alive/residency policy to reduce reload thrashing.

Add fair per-user queues, priority aging, bounded backlog, backoff, circuit breakers and retry limits. Retry eligible inference failures, not ambiguous tool mutations. Do not silently use cloud inference. Model pulls/deletions belong to administrative jobs, not the request path.

This scheduler distributes independent requests. It does not combine several Ollama nodes' memory into a single model instance.

## 9. Context management and quality

- Construct a task envelope containing objective, acceptance criteria, immutable source, effective grant, limits and allowed tools.
- Keep server policy separate from repository instructions and retrieved content. Repository text cannot authorize extra privileges.
- Reserve input space for tool schemas, next output and error handling. Use the effective tested context window, not the model's advertised maximum alone.
- Use exact tokenization when available; otherwise conservative estimates and measured guard bands.
- Start with lexical search and file reads. Add symbol indexing/embeddings only after retrieval evaluations show a benefit.
- Cache by tenant, snapshot digest, indexer version and embedding/model identity. Recheck access on every use.
- Read current exact contents before editing; summaries are not patch bases.
- Compact old history into a versioned summary preserving grants, source identities, evidence, failed attempts and unknowns. Preserve complete tool-call/result pairs.

Validate final structured output in code; valid JSON is not proof. Verify findings against the proper source revision. Record model judgment separately from execution evidence. Retain the original event ledger privately when summaries are used.

Local inference does not make the entire workflow local: code or patches returned through MCP may enter the hosted parent assistant's context. Provide explicit output policies such as summary-only, selected snippets or full patch export.

## 10. Security and authorization

Treat this as an execution service. MCP's security guidance includes credential misuse, confused-deputy behavior and SSRF [9]. Enforce capabilities independently of instructions.

| Boundary | Controls |
| --- | --- |
| Client → service | TLS, correct token audience, per-request job/repository authorization, rate limits, supported origin/host validation. |
| Service → worker | Authenticated connection, short-lived job credentials, tenant/job binding and fencing. |
| Worker → Ollama | Registered hosts/ports, validated resolved addresses, disabled arbitrary redirects, restricted network access. |
| Sandbox → infrastructure | No control-plane credentials, engine socket, host mounts, metadata endpoint or management-network egress. |
| Caller → artifact | Authorize every access; opaque IDs alone are insufficient; retention and download policy. |
| Model → tool | Schema, scope, budget and precondition checks for every call. |

Use non-root per-job containers with read-only base filesystems, dedicated writable workspaces, dropped privileges and enforced resource/network limits. Containers share a kernel; hostile public submissions or strong tenant separation warrant disposable VMs or a suitable hardened isolation runtime.

Filesystem confinement must handle symlinks, junctions/reparse points, alternate encodings, case behavior and races. Verify opened objects and enforce at the filesystem boundary; string-prefix checks are insufficient. Deny writes to repository metadata, mounted policy and unrelated paths.

Authenticate Ollama access at an appropriate gateway/network boundary; private HTTP does not imply per-user authorization. Do not pass frontend bearer tokens through to downstream services. Use separate scoped credentials. Redaction is secondary to preventing secret access.

Default build egress to denied; broker package restoration through approved sources. Do not put deployment/signing credentials in an execution sandbox. Keep approvals outside model control and bind them to exact actions and revisions.

## 11. Persistence and application interfaces

| Entity | Fields |
| --- | --- |
| Job | Tenant/owner, source, objective, grant version, limits, state, idempotency digest, retention. |
| Attempt | Worker, lease/fence, sandbox, model configuration, timing, parent/root and stop reason. |
| SourceSnapshot | Repository/base commit, tree digest, manifest and export provenance. |
| JobEvent | Monotonic sequence, type, time, attempt/revision and bounded payload/artifact reference. |
| ModelDeployment | Endpoint/model identity, qualification, health, measured performance and configured capacity. |
| InferenceReservation | Owner/fence, expiry, start/end and uncertain-outcome state. |
| ToolInvocation | Operation ID, validated arguments/digest, authorization, state and evidence. |
| Approval | Approver, job/action/revision binding, scope, expiry and consumption. |
| Artifact / ValidationRun | Hash/type/size, revisions, recipe/toolchain, exit/outcome and truncation. |

Use an append-only event journal with current-state projections; full event sourcing is optional. Store large transcripts, patches and logs outside ordinary event rows. Retention deletion must cover sources, results, transcripts, indexes and caches.

Suggested interfaces: `IJobService`, `IJobStore`, `IModelRegistry`, `IInferenceScheduler`, `IChatModelAdapter`, `IAgentRunner`, `IToolPolicy`, `ISandboxExecutor`, `IWorkspaceService`, `IArtifactStore`.

Suggested REST routes: create/status/events/cancel under `/api/agent-jobs`, job-scoped artifacts, and a separate authenticated approvals API. SSE or SignalR can stream UI events without owning execution lifetime.

### Illustrative application result envelope

```json
{
  "job_id": "job_example",
  "state": "completed",
  "stop_reason": "objective_attempted",
  "summary": "Added submission guard and regression test.",
  "source_snapshot_id": "snap_example",
  "output_revision": "revision_example",
  "validation": [{
    "recipe": "checkout-tests", "outcome": "passed",
    "exit_code": 0, "invocation_id": "tool_example"
  }],
  "artifacts": [{"artifact_id": "patch_example", "kind": "patch"}],
  "limitations": ["Browser end-to-end tests were not run."],
  "integration_status": "not_applied",
  "cleanup_status": "complete"
}
```

This is application data, not a protocol wire response. Distinguish passed, failed, not_run, timed_out and unknown validation outcomes.

## 12. Advanced orchestration

Earn complexity through measurements. Start with one competent agent and reliable tools, then add:

1. **Role profiles:** reviewer, investigator, implementer and validator with narrow capabilities.
2. **Independent review:** inspect source, patch and actual logs rather than relying on the implementer's narrative.
3. **Task graphs:** parallelize independent read-only exploration and integrate serially.
4. **Model routing:** use task type, fixture performance, validation failures and non-progress for escalation within qualified pools.
5. **Curated learning:** turn accepted/rejected outcomes into evaluation fixtures; do not make arbitrary chat history authoritative shared memory.

Initially disable recursive spawning. Later cap depth/children and share root budgets across all work. Give editing agents separate source copies; never let several mutate the same tree concurrently. Merged patches require combined validation. Model consensus is not independent verification.

Speculative parallel solutions cost GPU time and may fail similarly. Introduce them only when improved acceptance rates justify measured latency and compute. Self-reported confidence alone is a weak routing signal.

## 13. UI, telemetry and deployment

Show source identity, objective, grant, phase, node/model history, tool calls, actual validation, patch and cleanup status. Progress reports actions and observations, not private reasoning. Include repository/recipe administration, endpoint qualification, worker pools, quotas, approval inbox, retention and pause/stop controls.

| Metric family | Track |
| --- | --- |
| Service | Submission latency, queue age, active duration, approval wait, recovery and cleanup lag. |
| Inference | Cold load, first output, prefill/decode, token counts, reservations, overload and migrations. |
| Execution | Tool time, failures, denials, sandbox resources, truncation and timeouts. |
| Quality | Accepted patches, regressions, invented references, unnecessary edits, loops and user rework. |
| Audit | Artifact access, source export, approval/policy changes and retention deletion. |

Do not put source text or prompts in metric labels. Correlate job/attempt/inference/tool IDs with controlled cardinality. Distinguish inference time from build time. Keep transcript access explicitly governed.

Start with one control plane, durable database, private artifact storage, one execution host and existing Ollama nodes. Scale web replicas, execution capacity and inference capacity independently. Drain workers on upgrades, pin sandbox image digests and record toolchain changes.

## 14. Delivery milestones

| Milestone | Deliver | Exit criteria |
| --- | --- | --- |
| 0. Compatibility spike | Minimal MCP, one Ollama loop, fixture repository, host/auth matrix. | Each intended host submits, disconnects, polls and retrieves a result; timeouts and artifact integration are understood. |
| 1. Durable review | Jobs/events/artifacts, snapshots, read tools, budgets and cancellation. | Verified references; API restart preserves work; cross-user access denied; honest partial results. |
| 2. Sandboxed implementation | Isolation, patching, recipes, validation manifest and conflict-safe return. | Known defect fixed; regression passes; original source intact; drift never overwritten. |
| 3. Cluster scheduling | Inventory, qualification, reservations, fairness, backoff and residency policy. | Heterogeneous nodes execute concurrent work without service-side oversubscription; failure recovery does not replay mutations. |
| 4. Team operation | Permissions, approvals, quotas, retention, optional companion export and audit. | No cross-tenant access; no model-forged escalation; dirty export is reproducible. |
| 5. Advanced delegation | Reviewer, task graph and measured routing. | Documented quality gain over single-agent baseline at measured compute/latency cost. |

Do not postpone isolation or cancellation until after editing. Avoid calendar commitments until the current stack, identity system, worker OS and source-transfer requirements are known.

### Implementation backlog order

1. Version job/source/grant/tool/event/result schemas.
2. Build provider adapter and scripted fake-model test harness.
3. Implement transactional submission, leases, fences, cancellation and reaping.
4. Implement immutable importer and artifact store.
5. Implement sandbox supervisor and bounded read/search tools.
6. Implement conversation loop and durable invocation journal.
7. Expose six MCP tools plus UI/REST status.
8. Add patching, recipes, validation provenance and return path.
9. Add qualification, shared reservations, health and scheduling.
10. Add companion and advanced orchestration after baseline evaluation.

## 15. Validation strategy

Use scripted model responses for deterministic state/security tests and separate repeated live-model evaluations for quality. Record Ollama version, model digest/quantization/options, prompt/tool-schema versions, sandbox image and source fixture digest.

| Suite | Required cases |
| --- | --- |
| Runtime | Malformed calls, unknown tools, partial streams, oversized output, repeated loops, exhausted budget, cancellation during commands/inference. |
| Recovery | Crash around dispatch/result persistence, API restart, duplicates, expired lease, stale worker, unknown inference outcome, failed reaper. |
| Workspace | Traversal, links/junctions, races, dirty exports, new/deleted/binary files, line endings, base drift and conflicts. |
| Isolation | Secret access, management network, metadata endpoint, descendant processes, engine socket, hostile build scripts. |
| Authorization | Other tenant's job/artifact, forged or expired approval, arbitrary endpoint URL and redirect escape. |
| Scheduling | Cold starts, model mismatch, stale health, overload, fairness, replica races and external load. |
| Clients | Exact Cursor/Codex/ChatGPT builds: auth, tools, reconnect, polling, exports and optional Tasks support. |
| Quality | Known-defect review, bounded repair, strict edit scope, no-change task, misleading repository instructions, honest failed validation. |

**Proposed release gates:** no tested tenant/filesystem escape; no ambiguous mutation replay; no overwriting drifted local files; every test claim traces to recorded execution; every accepted job reaches a documented terminal/intervention state; cleanup failure remains visible. These are criteria to verify, not established guarantees.

Set model-quality thresholds after baseline measurement: repeated-run task success, unjustified edits, reference accuracy, p95 duration and GPU-seconds per accepted result. Preserve failed and inconclusive runs.

## 16. First implementation slice and open decisions

**Build first:** from Cursor, submit “Find and fix the failing test in fixture repository X at commit Y.” A worker creates an isolated copy, a qualified Ollama model reads and edits it, the service executes a named test recipe, and Cursor retrieves a patch with actual test output. Demonstrate cancellation, API restart and local base drift before adding multi-agent planning.

| Decision | Starting choice |
| --- | --- |
| Source | Registered repositories at exact commits; dirty snapshots later. |
| Execution | Dedicated Linux sandbox workers for compatible web builds. |
| Persistence | Proposed SQLite on a local persistent volume for a single coordinator, plus private artifact storage. |
| Inference | Native Ollama adapter, qualified pools, conservative reservations. |
| Delegation | Single agent; optional independent review afterward. |
| Integration | Patch and report; no automatic push/merge. |
| Connectivity | LAN access for local hosts; supported authenticated gateway/tunnel for hosted clients. |

Kickoff questions: backend/database; worker OS and isolation infrastructure; need for uncommitted IDE state; trusted team versus public tenants; exact model digests/hardware; intended host clients. These change deployment choices without changing the separation of control, inference, execution and integration.

## 17. PS5LM Companion integration and NuGet choices

This section takes precedence over generic Ollama-only assumptions elsewhere in
the plan. The current console backend is PS5LM / PS5LM Plus running llama-server,
exposed through OpenAI-compatible `/v1` APIs. Native Ollama servers are an
additional backend type, not an interchangeable console API.

### Existing components to preserve

- .NET 10 / ASP.NET Core, interactive server-rendered Blazor, MudBlazor,
  FastEndpoints, Serilog, and centrally managed package versions.
- A third-party-free `PS5LM.Companion.Core`. Keep SDK and persistence types behind
  domain interfaces; do not introduce MCP, AI, EF Core, or Docker types there.
- `InferenceRouter` in `Core/Inference` already performs model eligibility checks,
  bounded admission, per-console slot accounting, and lease release.
- `App/Inference/InferenceGateway.cs` forwards approved `/v1` paths through YARP,
  strips Companion credentials, and deliberately avoids forwarding retries.
- Console discovery/control remains FTP-first. A closed llama port before model
  loading is not evidence of a failed console.
- Existing transfers and configuration use JSON stores; there is no established
  relational job database to reuse. Agent persistence is a new subsystem.

The repository already has an inference gateway, so do not build a separate
load balancer for agents. Make gateway traffic, self-tests, and agent inference
share the same capacity authority. Agent job lifetime and inference-request
lifetime remain separate.

### Recommended package selection

| Package | Use | Placement / decision |
| --- | --- | --- |
| `ModelContextProtocol.AspNetCore` | HTTP MCP endpoint, tool schemas, discovery, DI integration | `App`; recommended |
| `Microsoft.Extensions.AI` | Provider-neutral chat messages, function tools, middleware | Agent implementation; recommended |
| `OpenAI` and `Microsoft.Extensions.AI.OpenAI` | OpenAI-compatible chat adapter for current llama-server backends | Infrastructure/agent adapter; recommended first provider |
| `OllamaSharp` | Native Ollama inference, inventory, and management; implements `IChatClient` | Add when genuine Ollama endpoints are connected |
| `Microsoft.Agents.AI` | Agent runtime and orchestration building blocks | Run an integration spike before adopting |
| `Microsoft.EntityFrameworkCore.Sqlite` | Jobs, events, checkpoints, grants, idempotency records | Infrastructure; proposed single-coordinator persistence |
| `CliWrap` | Git and recipe process execution, piping and cancellation | Execution worker; recommended |
| `Docker.DotNet` | Create and supervise container sandboxes through Docker API | Dedicated trusted worker if Docker is selected |
| `ModelContextProtocol.Extensions.Tasks` | Optional protocol-level asynchronous task support | After exact client/SDK compatibility is established |
| `Microsoft.Agents.AI.DurableTask` | Durable agent/workflow execution backed by Durable Task infrastructure | Evaluate later; requires an explicit persistence/backend deployment decision |

These are candidate package IDs, not a tested dependency lock. Verify current
release status, licenses, API compatibility, transitive versions, and target
platform support when implementing. Pin selected versions in
`Directory.Packages.props`. Do not add all candidates speculatively.

For the first implementation slice, select the MCP SDK, Microsoft.Extensions.AI,
the OpenAI-compatible adapter, SQLite, and CliWrap. Evaluate Agent Framework
early; add OllamaSharp and Docker support when their deployment paths are needed.

### Agent Framework decision gate

Evaluate `Microsoft.Agents.AI` before writing a complete custom agent runtime.
The spike must establish that it can support:

1. Policy-checked and durably journaled tool invocation.
2. An inference adapter that acquires the same admission leases as gateway
   requests and releases them correctly.
3. Persisted conversation state, cumulative budgets, cancellation, and approval
   pauses.
4. Correct tool calling against the actual PS5 model/server configurations.
5. Recovery without blindly repeating an interrupted mutation.

Adopt it if those integration points remain clear and testable. The application
still owns sandboxing, grants, source identity, patch integration, and evidence.
Ordinary session serialization and workflow checkpointing are not automatically
a durable distributed execution service. The DurableTask extension adds a
separate infrastructure choice that must be validated.

Avoid stacking Agent Framework, Semantic Kernel, and another agent framework
for overlapping behavior. Start with one runtime behind application interfaces.

### Inference integration

Use `IChatClient` inside the agent implementation. For current PS5 nodes, adapt
the OpenAI SDK chat client with a configured LAN endpoint. For native Ollama,
use OllamaSharp. Keep provider-specific operations separate: the PS5 FTP model
load/unload protocol is not an Ollama API operation.

Choose one admission path per request:

- An in-process adapter acquires `InferenceRouter` admission once and calls the
  selected upstream through a shared request implementation; or
- An adapter calls the gateway and lets the gateway acquire admission.

Do not acquire a lease and then call a gateway route that acquires another one.
With one slot per console, that can deadlock or time out behind itself. Prefer
extracting a reusable inference execution service while retaining YARP for the
external streaming gateway. Extend endpoint/provider abstractions when adding
non-console nodes; do not represent an Ollama server as a fake PS5 console.

The current router uses in-memory counters and synchronization. Keep one
admission authority initially. Multiple coordinators require shared reservations
or a single scheduling service; a second process does not share the first
process's slot counters.

The existing gateway intentionally avoids retries. Configure SDK and resilience
retry policies explicitly, because an SDK can repeat a generation even when
YARP does not. Preserve long prompt-processing timeouts and distinguish
inference retry from tool-execution replay. Test tools, streaming, cancellation,
usage reporting, and model identity on the exact llama-server builds.

### Persistence and worker hosting

Use SQLite on a local persistent volume for the first single-coordinator
deployment, with remote workers communicating through authenticated APIs.
Implement atomic job submission, claims, fencing, and invocation journaling;
EF Core supplies persistence access, not these semantics automatically.
Do not use a shared SQLite file over a network filesystem as a distributed queue.
Revisit PostgreSQL for multiple coordinators or measured write contention.

Reuse the hosted-service pattern for coordination, but retain dedicated
execution workers. `App/Program.cs` configures a three-second host shutdown
timeout for dashboard behavior. Accepted work must already be durable, and
worker termination, cleanup and recovery must not depend on the web host
remaining alive long enough to finish the job.

Hangfire is an alternative for durable dispatch and job administration, not an
additional default dependency. Its automatic retries need deliberate handling;
restarting an entire agent after an exception is not equivalent to safely
resuming an interrupted action. Select one owner of durable workflow state.

### Authentication and MCP route behavior

`App/Security/AccessTokenMiddleware.cs` recognizes `/api` and `/v1` for machine
responses but redirects other unauthenticated paths to browser login. Add
protocol-appropriate behavior for `/mcp`; an HTML login redirect is not an MCP
authentication implementation. Test the supported bearer/OAuth/tunnel flow
with each intended client.

The current shared token is a single-operator boundary, not individual identity
or tenant authorization. If team access is introduced, define actual principals,
repository grants, job ownership, approval authority, and artifact permissions.
Do not grant every MCP caller console administration or shell access implicitly.

### Suggested project boundaries

| Project | Responsibility |
| --- | --- |
| Existing `Core` | Dependency-free contracts, grants, job states, scheduling interfaces |
| Existing `Infrastructure` | Persistence, source/artifact stores, model-provider adapters |
| Existing `App` | MCP hosting, FastEndpoints management API, MudBlazor jobs UI |
| Proposed `Agents` library | Framework integration, context construction, tool orchestration |
| Proposed `AgentWorker` executable | Sandbox supervision and durable execution |

Reuse `Microsoft.Extensions.Http.Resilience`, Serilog, and existing UI
infrastructure. CliWrap manages processes; it is not a sandbox. Docker access
belongs only to a dedicated trusted worker, with no engine socket exposed
inside agent-controlled containers.

### Additional primary sources

- [MCP C# SDK and package inventory](https://github.com/modelcontextprotocol/csharp-sdk)
- [Microsoft.Extensions.AI / IChatClient](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient)
- [OpenAI .NET SDK: compatible endpoints and retry configuration](https://github.com/openai/openai-dotnet)
- [OllamaSharp](https://github.com/awaescher/OllamaSharp)
- [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
- [Agent Framework Ollama integration](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/ollama)
- [Agent Framework durable extension](https://learn.microsoft.com/en-us/agent-framework/hosting/durable-extension)
- [EF Core SQLite provider](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/)
- [CliWrap](https://github.com/Tyrrrz/CliWrap)
- [Docker.DotNet](https://github.com/dotnet/Docker.DotNet)
- [Hangfire retry behavior](https://docs.hangfire.io/en/latest/background-processing/dealing-with-exceptions.html)

## 18. Primary sources

Consulted 26 October 2026. Recheck version-sensitive details when pinning dependencies. Architecture, schemas, defaults and milestones above are recommendations.

1. [Ollama: Tool calling](https://docs.ollama.com/capabilities/tool-calling) — tool definitions, tool messages and iterative loop.
2. [Ollama: List models](https://docs.ollama.com/api/tags) — model inventory and digests.
3. [Ollama: List running models](https://docs.ollama.com/api/ps) — loaded-model observations.
4. [MCP: Transports](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports) — transport and backwards-compatibility considerations.
5. [MCP Tasks extension, 2026-07-28](https://modelcontextprotocol.github.io/ext-tasks/specification/2026-07-28/tasks.html) — async lifecycle and capability checks.
6. [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — .NET integration.
7. [Ollama: Chat API](https://docs.ollama.com/api/chat) — chat fields, tools, options and metrics.
8. [Ollama FAQ](https://docs.ollama.com/faq) — context, concurrency, queueing and residency controls.
9. [MCP Security Best Practices](https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/security_best_practices) — credential, SSRF and deputy concerns.
10. [Cursor: MCP](https://cursor.com/docs/mcp) — client configuration and tool behavior.
11. [OpenAI: Custom MCP servers](https://developers.openai.com/api/docs/guides/custom-mcp-server) — hosted-client connection options.

[1]: https://docs.ollama.com/capabilities/tool-calling
[2]: https://docs.ollama.com/api/tags
[3]: https://docs.ollama.com/api/ps
[4]: https://modelcontextprotocol.io/specification/2026-07-28/basic/transports
[5]: https://modelcontextprotocol.github.io/ext-tasks/specification/2026-07-28/tasks.html
[6]: https://github.com/modelcontextprotocol/csharp-sdk
[7]: https://docs.ollama.com/api/chat
[8]: https://docs.ollama.com/faq
[9]: https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/security_best_practices
