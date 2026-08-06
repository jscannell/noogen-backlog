---
name: backlog
description: Read and update the Noogen company backlog — a WSJF-prioritised Kanban board of work items stored in Google Drive. Use whenever asked what to work on next, what is in flight, how work is progressing, or to file, score, start, block, or finish a ticket. Triggers on "backlog", "work item", "ticket", "what's next", "WSJF", "priorities", "what am I working on", "WIP", "cycle time".
---

# Noogen backlog

Work items live in a Google Drive shared drive: one markdown document per ticket, indexed by a
Google Sheet. Reach everything through the `backlog` CLI — never edit the Sheet directly, and
never hand-write a ticket document.

Add `--json` to any command for machine-readable output. Prefer it when you need to parse.

## The model

Work moves through three Kanban columns, and **the tab a ticket lives on is its state**:

| tab | meaning | ranked? |
|---|---|---|
| `Backlog` | not started | **yes — WSJF order** |
| `In Progress` | pulled, not finished (`in-progress` / `in-review` / `blocked`) | no |
| `Archive` | terminal (`done` / `cancelled` / `duplicate`) | no |

## Commands

```
backlog list [--area A] [--owner O] [--top N]   # the queue, rank order
backlog next [--owner me]                       # highest-ranked item
backlog wip [--owner O]                         # in flight, oldest first, flags aging
backlog show <id>                               # one ticket including its body
backlog flow [--since 90d]                      # throughput, cycle-time p50/p85

backlog new --title "..." [--type feature|bug|chore|spike] [--area A] [--owner O]
            [--bv N --tc N --rroe N --size N] [--description "..."]
backlog edit <id> [--title ...] [--area ...] [--owner ...] [--type ...]
backlog score <id> [--bv N] [--tc N] [--rroe N] [--size N]
backlog note <id> --text "..."                  # appends to the Activity Log

backlog start <id> [--owner me] [--force]       # Backlog -> In Progress
backlog block <id> --reason "..." | backlog unblock <id> | backlog review <id>
backlog archive <id> --as done|cancelled|duplicate [--note "..."]
backlog restore <id>                            # Archive -> Backlog

backlog doctor | backlog reindex                # health check / repair
```

## Times

Human output renders in the backlog's configured timezone; `--utc` opts out. **`--json` is always
UTC** — parse that, and convert for display rather than assuming the human-readable form.

Ticket documents deliberately carry **no timestamps, phase, or work state** in their frontmatter.
Those live in the Sheet, in Drive's own file metadata, and narratively in the Activity Log. If
asked when something started, use `backlog show <id>` rather than reading the document.

## Rules

1. **Archive, never delete.** `backlog archive` moves the row and the document. Nothing is ever
   trashed. If asked to delete a ticket, archive it as `cancelled` or `duplicate` instead.

2. **Go through the CLI.** The Sheet owns the `cod`, `wsjf`, and `rank` formulas. Writing those
   cells by hand breaks the index. `backlog doctor` detects the damage; `backlog reindex` repairs it.
   Editing a ticket *document* by hand is fine and expected — it holds only human-editable
   content, and `reindex` folds those edits back into the index.

3. **Only unstarted work is ranked.** WSJF sequences what to *start*, so `score` is refused on a
   started item and its numbers freeze as history. Do not try to re-prioritise work in flight —
   if it genuinely should stop, `restore` it to the backlog and say so.

4. **Respect the WIP limit.** `start` refuses to exceed it. That refusal is the system working,
   not an obstacle. Report it and suggest finishing something; only use `--force` if the person
   explicitly asks after being told.

5. **Verbs are the transitions.** There is no `--status` flag. Use `start`, `block`, `unblock`,
   `review`, `archive`, `restore`.

## Scoring

WSJF = (business value + time criticality + risk reduction/opportunity enablement) ÷ job size,
each on the modified-Fibonacci scale `1, 2, 3, 5, 8, 13, 20`.

Scores are **relative, not absolute** — the smallest item in each column should be a 1. When
scoring, read `references/wsjf.md` for what each dimension means and how to calibrate against
items already in the queue.

Never invent scores silently. If asked to file a ticket without enough information to score it,
create it unscored (it will sort last) and say it needs scoring.

## Setup

If a command reports no backlog is configured, the person needs to run
`backlog init --drive <sharedDriveId>` once. See the repo README for the service-account setup;
do not attempt to create credentials yourself.
