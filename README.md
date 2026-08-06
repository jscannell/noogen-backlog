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

## Times

Timestamps are stored as **real Sheets datetime cells** and the spreadsheet's timezone is set to
the backlog's configured one, so the Sheet renders local times natively, sorts correctly across
DST, and supports date filters. Set it with `backlog init --timezone America/New_York`; it lives
on the Config tab and `backlog doctor` flags a mismatch between it and the spreadsheet's own
setting, because the two disagreeing would shift every stored instant.

The CLI renders human output in that timezone and `--utc` opts out. **`--json` is always UTC** —
it is the machine contract, and a representation that moved with a config setting would be a trap.

Two honest caveats. A datetime serial is a wall-clock value, so the repeated hour at DST fall-back
is ambiguous on read-back; it resolves to the earlier instant, bounded at one hour, once a year,
on metrics reported in days. And serials are quantised to whole seconds, since a fractional day in
a double cannot represent most instants exactly.

## Install

```bash
dotnet pack src/Noogen.Backlog.Cli/Noogen.Backlog.Cli.csproj -c Release
dotnet tool install -g --add-source ./artifacts Noogen.Backlog.Cli
backlog help
```

Installing globally is deliberate — the backlog spans repos, so the tool should not live inside
any one of them.

## Authentication

Each person signs in as themselves:

```bash
backlog login          # opens a browser, once
backlog whoami         # who you are and how you authenticated
backlog logout         # revokes with Google and deletes the local token
```

No gcloud, no shared credentials, no service-account key on anyone's laptop. Drive revision
history attributes edits to the actual human, and access is governed by their existing membership
of the shared drive rather than granted centrally.

Credentials resolve in this order, reflecting who each source is for:

| order | source | for |
|---|---|---|
| 1 | `NOOGEN_BACKLOG_CREDENTIALS` — service-account key path | CI, automation, headless |
| 2 | signed-in user (`backlog login`) | a person at a keyboard |
| 3 | Application Default Credentials | Workload Identity in GKE, for the platform agent |

ADC is last and never volunteered. On a workstation it is machine-global and usually belongs to
something else — running `gcloud auth application-default login --scopes=…drive` would clobber the
credentials you already use for Vertex and Cloud SQL. This tool never asks you to do that.

### Token security

The refresh token is encrypted at rest by the OS keystore — **DPAPI** on Windows, **Keychain** on
macOS, **Secret Service** on Linux. `backlog whoami` reports which is in use, and warns loudly if
none is available rather than quietly writing plaintext.

**The threat this addresses.** A Drive-scoped refresh token is a high-value target, and the
dominant real-world attack is commodity infostealer malware sweeping known credential paths and
exfiltrating the files for offline use. A copied file is useless here: the key material never
leaves the OS keystore and is bound to that user on that device.

**What it does not address, stated plainly.** Malware already running as you, on your machine,
while the keystore is unlocked, can ask the OS to decrypt exactly as we do. No user-space scheme
prevents that — a process with your privileges can do what you can do. The honest claim is that
this raises the cost from "copy a file" to "write target-specific code and run it on the victim's
machine", and eliminates offline reuse. Anything storing a key beside the ciphertext would be
theatre; this does not.

**If a machine is compromised**, run `backlog logout` (revokes server-side), or revoke at
[myaccount.google.com/permissions](https://myaccount.google.com/permissions). A Workspace admin can
revoke org-wide under Security → API Controls → App Access Control.

**Scope.** The CLI requests full `drive` because the narrower `drive.file` scope only covers files
the app itself created — a second person could not read the index or tickets your install created.
That breadth is the reason token protection matters, and worth weighing before rollout.

### One-time OAuth client (once per organisation)

1. Cloud console → APIs & Services → **Credentials** → Create Credentials → **OAuth client ID**
2. Application type: **Desktop app**
3. Consent screen → User Type: **Internal** — internal apps skip Google's verification review,
   which matters because `drive` is a restricted scope
4. Download the JSON to `%APPDATA%\Noogen\oauth.json` (the file Google gives you works unedited),
   or set `NOOGEN_BACKLOG_OAUTH_CLIENT_ID` / `NOOGEN_BACKLOG_OAUTH_CLIENT_SECRET`

The client secret is not confidential here, and Google says so: for installed apps it cannot be
kept secret, and security rests on the user's own consent plus the loopback redirect. The real
access boundary is each person's Google account and their shared-drive membership.

## One-time Google setup

1. Enable the **Drive API** and **Sheets API** on the GCP project.
2. Create the OAuth client described above — once, for the whole organisation.
3. Make sure everyone who needs the backlog is a member of the shared drive. That, not any grant
   inside this tool, is what controls access.
4. Each person runs `backlog login`.
5. `backlog init --drive <sharedDriveId> [--timezone America/New_York]` — creates the folders,
   the index, the tabs, the formulas, and the Config tab, and stamps the timezone onto the
   spreadsheet. It is idempotent, so re-running repairs a half-finished setup. The timezone
   defaults to this machine's on first run.

Note there is no service-account key to create or distribute, so the
`constraints/iam.disableServiceAccountKeyCreation` org policy is not in the way. If you *do* want
a key for CI, add the service account as a **Content manager** on the shared drive — shared drives
accept one as a direct member, so no domain-wide delegation is involved — and point
`NOOGEN_BACKLOG_CREDENTIALS` at the key file.

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

backlog init --drive <sharedDriveId> [--timezone America/New_York]
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
