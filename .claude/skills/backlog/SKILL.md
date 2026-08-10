---
name: backlog
description: Read and update the Noogen company backlog — a WSJF-prioritised Kanban board of work items stored in Google Drive. Use whenever asked what to work on next, what is in flight, how work is progressing, or to file, score, start, block, or finish a ticket. Triggers on "backlog", "work item", "ticket", "what's next", "WSJF", "priorities", "what am I working on", "WIP", "cycle time".
---

# Noogen backlog

Work items live in a Google Drive shared drive: one Google Doc per ticket, written and read as
markdown, indexed by a Google Sheet. Reach everything through the `backlog` CLI — never edit the
Sheet directly, and never hand-write a ticket document.

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
            [--bv N --tc N --rroe N --size N]
            [--description "..."] [--acceptance-criteria "..."]
backlog edit <id> [--title ...] [--area ...] [--owner ...] [--type ...]
                  [--description "..."] [--acceptance-criteria "..."]
backlog score <id> [--bv N] [--tc N] [--rroe N] [--size N]
backlog note <id> --text "..."                  # appends to the Activity Log

# both prose options also come off the command line entirely — see below
backlog new --title "..." --description-file body.md --acceptance-criteria-file ac.md
Get-Content body.md -Raw | backlog new --title "..." --description -

# score flags also accept their spelled-out forms, which is what the Sheet's
# columns are called: --business-value --time-criticality --risk-opportunity --job-size

backlog start <id> [--owner me] [--force]       # Backlog -> In Progress
backlog block <id> --reason "..." | backlog unblock <id> | backlog review <id>
backlog archive <id> --as done|cancelled|duplicate [--note "..."]
backlog restore <id>                            # Archive -> Backlog

backlog doctor | backlog reindex                # health check / repair
```

## Times

Human output renders in the backlog's configured timezone; `--utc` opts out. **`--json` is always
UTC** — parse that, and convert for display rather than assuming the human-readable form.

Ticket documents deliberately carry **no timestamps, phase, or work state**. Those live in the
Sheet, in Drive's own file metadata, and narratively in the Activity Log. If asked when something
started, use `backlog show <id>` rather than reading the document.

## The ticket document

An `# <ID> — <Title>` heading, then `- **Key:** value` bullets for type, area, owner and the four
scores, then the prose sections. The heading is the only place the id and title live.

`backlog show <id>` prints the prose from the first section onwards — the heading and the bullets
would only repeat the summary line it already prints above them.

**The CLI writes the heading and the bullets; below them it touches three things.**
`--description` replaces the `## Description` section, `--acceptance-criteria` replaces the
`## Acceptance Criteria` section, and `note` appends to the Activity Log. Everything else —
`## Notes`, anything a person added — is theirs, so to change one of those, give them the document
URL from `backlog show <id>` to edit in Docs.

Both prose options **replace** the whole section, so read the current text with
`backlog show <id>` first and pass the whole new version, not just the part that changed. Passing
`""` is refused rather than treated as "empty it". Neither writes an Activity Log entry; if the
change is worth recording, follow it with `note`.

### Acceptance criteria are yours to write, not to leave as *TODO*

A ticket filed with no acceptance criteria says `- [ ] *TODO*`, which reads to everyone downstream
as a ticket that is ready when nobody has said what "done" means. **Write them.** You do not need
permission, and you do not need to hand anyone a Docs link — `--acceptance-criteria` is a
first-class flag on both `new` and `edit`.

Write them as a markdown checklist, one line per condition, each one something a person could
check without asking you what you meant:

```
- [ ] `backlog doctor` reports the duplicate row and exits 1
- [ ] the row survives a restart
- [ ] README documents the new flag
```

If you genuinely cannot tell what "done" means — the request was one line and you have no other
context — file the ticket anyway rather than blocking, then **say in your reply that the criteria
are still `*TODO*` and what you would need to write them.** `backlog new` prints that reminder on
stderr for the same reason. Do not silently leave the placeholder and report the ticket as filed.

The same goes for the description: file with one, or say that you did not.

### Writing a description or a set of criteria

**Pass anything longer than a line as `--description-file <path>` or
`--acceptance-criteria-file <path>`, not inline.** Write the prose to a temporary file and point at
it. A checklist is almost always longer than a line, so this is the normal path for criteria.

The reason is the shell, not the tool. Windows hands a native process one command-line string, and
PowerShell does not escape a double quote inside an argument it quotes — so a description
containing one is split, and everything after the first quote arrives as separate arguments. That
is refused now (exit 2), but the ticket still does not get filed. A file path is one argument
whatever the file contains, so it cannot be split. `--description -` reads standard input for the
same reason; use it when the text is already in a pipe.

Every path refuses an empty file or pipe, and giving one section both its flags at once is an
error. Only one option per command may read standard input — there is one pipe — so when both
sections are long, give at least one of them as a file.

Every verb rejects an option it does not read and any positional argument beyond a ticket id
(exit 2, kind `usage`), so a flag borrowed from another verb — or a fragment of a value the shell
split — fails loudly rather than being ignored.

An option that takes a value must be given one: a bare `--title` is an error, not "leave the title
alone", and an option will not swallow another option as its value (`note NG-12 --text --json`
errors instead of writing a note that says `--json`). To pass a value that genuinely begins with
two dashes, join it with `=`: `--title=--draft`.

## Rules

1. **Archive, never delete.** `backlog archive` moves the row and the document. Nothing is ever
   trashed. If asked to delete a ticket, archive it as `cancelled` or `duplicate` instead.

2. **Go through the CLI.** The Sheet owns the `Cost of Delay`, `WSJF`, and `Rank` formulas. Writing
   those cells by hand breaks the index. `backlog doctor` detects the damage; `backlog reindex` repairs it.
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

## Setup and authentication

Each person signs in as themselves with `backlog login`, which opens a browser once. If a command
fails with `not-signed-in`, tell them to run it — do not attempt to authenticate on their behalf,
and never read, copy, print, or move anything under the credentials directory.

A command may pause and print `Google is rate limiting requests; waiting …` on stderr. That is
normal — it retries on its own. If one still fails with `rate-limited`, nothing was written: a
throttled request is rejected, not half-applied. Wait a minute and run it again rather than
retrying in a tight loop, and never work around it by falling back to editing the Sheet directly.

If a command reports no backlog is configured, someone needs to run
`backlog init --drive <sharedDriveId> --timezone <IANA>` once. See the repo README for the
one-time OAuth client setup; do not attempt to create credentials yourself.
