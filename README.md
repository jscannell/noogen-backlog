# Noogen backlog

A lightweight, company-wide work-item tracker: **markdown tickets in a Google Drive shared drive,
indexed by a Google Sheet, prioritised with WSJF, run as Kanban.** Readable and editable by humans
in Drive; queryable and writable by agents through the `backlog` CLI.

Completed work is **archived, never deleted**.

## Why a CLI

The Drive MCP connector is read-and-create only — no update, no move, no delete, and no Sheets
API at all. An agent could file a ticket through it but never edit one, never archive one, and
never touch the index. So access goes through this first-party CLI, backed by a service account.

## The model

Work moves through three Kanban columns, and **the tab a ticket lives on is its state**:

| tab | phase | ranked? | holds |
|---|---|---|---|
| `Backlog` | not started | **yes — the WSJF queue** | everything unstarted; unscored sorts last |
| `In Progress` | pulled, not finished | no | `state` ∈ `in-progress`, `in-review`, `blocked` |
| `Archive` | terminal | no | `outcome` ∈ `done`, `cancelled`, `duplicate` |

Because the tab is the state, "is this ranked?" is a property of *where the row lives* rather than
a flag anyone has to maintain. `cod`, `wsjf`, and `rank` are live Sheet formulas on `Backlog`
only; once work starts the scores freeze as static values — a historical record for calibrating
estimates, never recomputed.

Drive layout:

```
Noogen Backlog/                     (shared drive)
├── Backlog Index                   Google Sheet — Backlog / In Progress / Archive / Config
├── tickets/
│   └── NG-0007-wsjf-index-tool.md
└── archive/2026/Q3/
    └── NG-0003-slack-thread-replay.md
```

The `title` cell in the Sheet links to the ticket document. It is a rich-text link rather than a
`HYPERLINK()` formula, so the stored cell value stays the plain title and ordinary reads are
unaffected.

## Install

```bash
dotnet pack src/Noogen.Backlog.Cli/Noogen.Backlog.Cli.csproj -c Release
dotnet tool install -g --add-source ./artifacts Noogen.Backlog.Cli
backlog help
```

Installing globally is deliberate — the backlog spans repos, so the tool should not live inside
any one of them.

## One-time Google setup

1. Enable the **Drive API** and **Sheets API** on the GCP project.
2. Create a service account, e.g. `noogen-backlog@<project>.iam.gserviceaccount.com`.
   **No project IAM roles are needed.**
3. Add that service-account email as a **Content manager** on the shared drive. Shared drives
   accept a service account as a direct member, so no domain-wide delegation is involved.
4. Point `GOOGLE_APPLICATION_CREDENTIALS` at its JSON key.
5. `backlog init --drive <sharedDriveId>` — creates the folders, the index, the tabs, the
   formulas, and the Config tab. It is idempotent, so re-running repairs a half-finished setup.

> If step 4 is blocked by the `constraints/iam.disableServiceAccountKeyCreation` org policy, use
> your own credentials instead — no code change is needed, because auth resolves through the ADC
> chain:
> ```bash
> gcloud auth application-default login \
>   --scopes=openid,email,https://www.googleapis.com/auth/drive,https://www.googleapis.com/auth/spreadsheets
> ```

Local config lives at `%APPDATA%\Noogen\backlog.json` and holds only the spreadsheet id.
Everything else — WIP limit, id prefix, folder ids, vocabularies — lives on the Sheet's **Config**
tab, so the whole company shares one answer and the process policy is visible to everyone.

Overridable by `NOOGEN_BACKLOG_SPREADSHEET_ID`, `NOOGEN_BACKLOG_DRIVE_ID`, `NOOGEN_BACKLOG_OWNER`.

## Commands

```
backlog list [--area A] [--owner O] [--top N]   # the queue, rank order
backlog next [--owner me]                       # highest-ranked item
backlog wip [--owner O]                         # in flight, oldest first, flags aging
backlog show <id>
backlog flow [--since 90d]                      # throughput, cycle-time p50/p85

backlog new --title "..." [--type feature|bug|chore|spike] [--area A] [--owner O]
            [--bv N --tc N --rroe N --size N] [--description "..."]
backlog edit <id> [--title ...] [--area ...] [--owner ...] [--type ...]
backlog score <id> [--bv N] [--tc N] [--rroe N] [--size N]
backlog note <id> --text "..."

backlog start <id> [--owner me] [--force]
backlog block <id> --reason "..." | backlog unblock <id> | backlog review <id>
backlog archive <id> --as done|cancelled|duplicate [--note "..."]
backlog restore <id>

backlog init --drive <sharedDriveId>
backlog doctor | backlog reindex
```

Every command accepts `--json`. That flag is the machine contract, and it is what makes the tool
usable by agents: the skill teaches the CLI surface, not the storage layout.

Verbs *are* the transitions — there is deliberately no `--status` flag to pass an illegal value
to, and `edit --status` errors with a pointer to the right verb.

## WSJF

`WSJF = (bv + tc + rroe) / size`, each on the modified-Fibonacci scale `1, 2, 3, 5, 8, 13, 20`,
scored **relatively** — the smallest item in each column should be a 1. Full rubric in
[.claude/skills/backlog/references/wsjf.md](.claude/skills/backlog/references/wsjf.md).

## Kanban

- **WIP limit** on the Config tab. `backlog start` refuses to exceed it and names what is already
  in flight; `--force` overrides and records that it did.
- **Flow metrics** come free from timestamps already stored: lead time is `created → archived`,
  cycle time is `started → archived`, both frozen onto the Archive row. `backlog flow` reports
  throughput and p50/p85.
- **Aging WIP**: `backlog wip` shows age since start and flags anything past the p85 cycle time.

No sprint or iteration fields exist, by design. `in-review` is a state on In Progress rather than
its own column; if review later needs its own WIP limit, promoting it to a fourth tab is a
`BacklogPhase` change that callers do not see.

## Projects

| project | role |
|---|---|
| `Noogen.Providers.GoogleWorkspace` | Drive + Sheets client factories and gateways; ADC-chain auth |
| `Noogen.Backlog` | all the logic — store, lifecycle, index, documents, metrics |
| `Noogen.Backlog.Cli` | thin arg-parsing shell over `IBacklogStore`, packaged as a global tool |
| `Noogen.Backlog.Tests` | xUnit against in-memory Drive/Sheets fakes |

The logic deliberately lives in a library rather than the CLI: the Noogen platform agent is a
future consumer, and adding a `BacklogToolset` there should mean writing `[AgentTool]` wrappers
over `IBacklogStore`, not reimplementing any of this. Auth resolving through the ADC chain means
the same code path serves a key file today and Workload Identity in GKE later.

## Development

```bash
dotnet build src/Noogen.Backlog.slnx
dotnet test  src/Noogen.Backlog.Tests
```

Conventions: no tuples, no primary constructors, no `is { }` patterns. Warnings are errors.
Commits follow [Conventional Commits](https://www.conventionalcommits.org/).
