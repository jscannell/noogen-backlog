---
name: backlog
description: Read and update the Noogen company backlog — a WSJF-prioritised Kanban board of work items stored in Google Drive. Use whenever asked what to work on next, what is in flight, how work is progressing, or to file, score, start, block, or finish a ticket. Triggers on "backlog", "work item", "ticket", "what's next", "WSJF", "priorities", "what am I working on", "WIP", "cycle time".
---

# Noogen backlog

Work items live in a Google Drive shared drive: one Google Doc per ticket, written and read as
markdown, indexed by a Google Sheet. Reach everything through the `backlog` CLI — never edit the
Sheet directly, and never hand-write a ticket document.

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
backlog find "<text>" [--area A] [--top N]      # search every tab, names and prose
backlog show <id> [--section S] [--full]        # one ticket and its body
backlog flow [--since 90d]                      # throughput, cycle-time p50/p85

backlog new --title "..." [--type feature|bug|chore|spike] [--area A] [--owner O]
            [--bv N --tc N --rroe N --size N]
            [--description "..."] [--acceptance-criteria "..."]
backlog edit <id> [--title ...] [--area ...] [--owner ...] [--type ...]
                  [--description "..."] [--acceptance-criteria "..."] [--note "..."]
backlog score <id> [--bv N] [--tc N] [--rroe N] [--size N]
backlog note <id> --text "..."                  # appends to the Activity Log

# every prose option comes off the command line entirely — see below.
# --description, --acceptance-criteria, --note, --text and --reason each also
# answer to --<name>-file <path>, and to --<name> - for a pipe.
backlog new --title "..." --description-file body.md --acceptance-criteria-file ac.md
backlog edit <id> --description-file new.md --note-file why.md
Get-Content body.md -Raw | backlog new --title "..." --description -

# score flags also accept their spelled-out forms, which is what the Sheet's
# columns are called: --business-value --time-criticality --risk-opportunity --job-size

backlog start <id> [--owner me] [--force]       # Backlog -> In Progress
backlog block <id> --reason "..." | backlog unblock <id> | backlog review <id>
backlog archive <id> --as done|cancelled|duplicate [--note "..."]
backlog restore <id>                            # Archive -> Backlog

backlog doctor | backlog reindex                # health check / repair
```

## Read cheaply

Backlog output is large and you pay for all of it. The same answer differs by more than 10x
depending on how you ask, so ask narrowly:

- **Answer the question that was asked.** `next` and `wip` cost ~20 tokens. `list` costs ~1,300 and
  `list --json` ~4,300. "What should I work on?" is `next`, not `list`.
- **Cap the queue.** `list --top 10` is a fifth of a bare `list`. Ask for all 40+ rows only when the
  question genuinely spans the whole backlog.
- **Human output is the default.** The table carries the same facts as `--json` at a quarter of the
  size, and you can read it. Reach for `--json` only when you need a field the table does not show
  — then narrow it: `--fields id,wsjf,title` on `list`, `next`, `wip`, and `find`.
- **Looking for a particular ticket is `find`, never `list`.** Pulling the whole queue to string-match
  it yourself costs ~4,300 tokens, misses everything in flight or archived, and cannot see prose at
  all. `find "rate limit"` costs a fraction of that and searches all three tabs.
- **`show` before an edit is `show --section`.** `--section description`,
  `--section acceptance-criteria`, `--section notes`, `--section activity-log`, or any heading the
  document has. One section instead of a whole document.
- **`show` trims the Activity Log** to the last few entries; on a ticket that has been worked, the
  log is most of the body. `--full` prints all of it and is rarely what you want.

## Search before you file

`backlog new` will happily file the ticket that already exists. **Run `find` first** whenever you
are about to file something, or whenever you are asked whether the backlog covers a topic. It is
the only verb that spans all three tabs and the only one that reads a ticket's prose.

```
backlog find "rate limit"
backlog find "rate limit" --json --fields id,title,phase,match
```

Every hit says which of two sources matched, and they behave differently:

- **`name`** — a substring of the id, title, area or owner, from the index. Exact and immediate, so
  a ticket filed a minute ago is found by its title.
- **`body`** — the document's text, from Drive's full-text index. This is the only way to reach a
  description or acceptance criteria, and it has three limits: it matches **whole words**
  (`find limit` does not match *limiting*), it may **not yet know** about a document written in the
  last few minutes, and it searches the **whole document** including the Activity Log.

So a `find` that returns nothing means "no ticket names this and no indexed document contains this
word" — not "no such ticket". If a search comes back empty and you are unsure, try the distinctive
noun on its own rather than a phrase, and say what you searched for when you report the result.

The search text is one positional argument, so quote it. If the shell splits it the command exits 2
rather than searching for a fragment.

## Do not verify writes

**Exit 0 is the confirmation.** A command that succeeded did what it said, so do not `show` the
ticket again to check, and do not run `doctor` after a write. `doctor` is for suspected damage, not
for reassurance.

There is no half-written state to check for. A rate-limited request is *rejected*, never partly
applied, so a command that fails with `rate-limited` wrote nothing.

## Times

Human output renders in the backlog's configured timezone; `--utc` opts out. **`--json` is always
UTC** — parse that, and convert for display.

Ticket documents carry **no timestamps, phase, or work state**: those live in the Sheet, in Drive's
file metadata, and narratively in the Activity Log. If asked when something started, use
`backlog show <id>` rather than reading the document.

## The ticket document

An `# <ID> — <Title>` heading, then `- **Key:** value` bullets for type, area, owner and the four
scores, then the prose sections. The heading is the only place the id and title live, and `show`
prints the prose from the first section onwards.

**The CLI writes the heading and the bullets; below them it touches three things.**
`--description` replaces `## Description`, `--acceptance-criteria` replaces
`## Acceptance Criteria`, and `note` appends to the Activity Log. Everything else — `## Notes`,
anything a person added — is theirs, so to change one of those, give them the document URL from
`backlog show <id>` to edit in Docs.

Both prose options **replace** the whole section, so read the current text first and pass the whole
new version, not just the part that changed:

```
backlog show NG-12 --section description       # just that section
backlog edit NG-12 --description-file new.md --note "why it changed"
```

Passing `""` is refused rather than treated as "empty it". An edit writes no Activity Log entry on
its own — `--note` records one in the same write, instead of a second `note` command.

### Acceptance criteria are yours to write, not to leave as *TODO*

A ticket filed with no acceptance criteria says `- [ ] *TODO*`, which reads to everyone downstream
as ready when nobody has said what "done" means. **Write them.** You do not need permission, and
you do not need to hand anyone a Docs link — `--acceptance-criteria` is a first-class flag on both
`new` and `edit`. Write a markdown checklist, one line per condition, each one something a person
could check without asking what you meant:

```
- [ ] `backlog doctor` reports the duplicate row and exits 1
- [ ] the row survives a restart
- [ ] README documents the new flag
```

If you genuinely cannot tell what "done" means, file the ticket anyway rather than blocking, then
**say in your reply that the criteria are still `*TODO*` and what you would need to write them**
(`backlog new` prints that reminder on stderr too). Never leave the placeholder silently and report
the ticket as filed. The same goes for the description: file with one, or say that you did not.

### Giving prose to the CLI

**Pass anything longer than a line as `--description-file <path>` or
`--acceptance-criteria-file <path>`, not inline** — a checklist almost always qualifies.
`--description -` reads stdin instead; only one option per command may.

The reason is the shell, not the tool: PowerShell splits an argument at an embedded double quote,
and a file path cannot be split whatever it contains. The CLI refuses a split value with exit 2
rather than filing a truncated ticket — if you see that, switch to a file. `references/prose-input.md`
has the full rules (empty input, values beginning with two dashes, one stdin reader per command).

**Every prose option takes a file.** `--description`, `--acceptance-criteria`, `--note`, `--text`
and `--reason` each answer to `--<name>-file <path>` and to `--<name> -`. A note explaining why an
edit happened is the one most likely to be long and to quote somebody, so give it as a file:
`backlog edit NG-12 --description-file new.md --note-file why.md`.

**Headings inside a section start at `###`.** A section ends at the next heading of its own level
or higher, so a `##` in a description or a set of criteria is a sibling of `## Description` rather
than part of it. The CLI refuses one and names the heading; write `### Problem`, not `## Problem`.
Deeper levels nest as you would expect, and Docs renders them the same way.

## Rules

1. **Archive, never delete.** Nothing is ever trashed. If asked to delete a ticket, archive it as
   `cancelled` or `duplicate` instead.

2. **Go through the CLI.** The Sheet owns the `Cost of Delay`, `WSJF`, and `Rank` formulas, and
   writing those cells by hand breaks the index — `doctor` detects it, `reindex` repairs it.
   Editing a ticket *document* by hand is fine and expected; `reindex` folds those edits back in.

3. **Only unstarted work is ranked.** WSJF sequences what to *start*, so `score` is refused on a
   started item and its numbers freeze as history. Do not re-prioritise work in flight — if it
   genuinely should stop, `restore` it to the backlog and say so.

4. **Respect the WIP limit.** `start` refuses to exceed it, and that refusal is the system working.
   Report it and suggest finishing something; only use `--force` if the person explicitly asks
   after being told.

5. **Verbs are the transitions.** There is no `--status` flag. Use `start`, `block`, `unblock`,
   `review`, `archive`, `restore`.

## Scoring

WSJF = (business value + time criticality + risk reduction/opportunity enablement) ÷ job size,
each on the modified-Fibonacci scale `1, 2, 3, 5, 8, 13, 20`.

Scores are **relative, not absolute** — the smallest item in each column should be a 1. When
scoring, read `references/wsjf.md` for what each dimension means and how to calibrate.

Never invent scores silently. If asked to file a ticket without enough information to score it,
create it unscored (it sorts last) and say it needs scoring.

## Setup and authentication

Each person signs in as themselves with `backlog login`, which opens a browser once. If a command
fails with `not-signed-in`, tell them to run it — do not authenticate on their behalf, and never
read, copy, print, or move anything under the credentials directory.

A command may pause and print `Google is rate limiting requests; waiting …` on stderr. That is
normal; it retries on its own. Never work around a rate limit by editing the Sheet directly.

If a command reports no backlog is configured, someone needs to run
`backlog init --drive <sharedDriveId> --timezone <IANA>` once. See the repo README for the
one-time OAuth client setup; do not attempt to create credentials yourself.
