---
name: backlog
description: The Noogen backlog - a WSJF-prioritized Kanban board of work tickets. File, score, start, block or finish a work item; answer what's next, what is in flight, WIP, priorities, cycle time.
---

# Noogen backlog

Every ticket is a document of prose, indexed by a spreadsheet, and `backlog` holds both: `show`
returns a ticket's whole text and `find` searches it, so reading one needs nothing else. Reach
everything through the tool — never edit the index directly, and never hand-write a ticket
document.

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
backlog show <id> [--section S] [--full]        # one ticket and the text of its document
backlog flow [--since 90d]                      # throughput, cycle-time p50/p85

backlog new --title "..." [--type feature|bug|chore|spike] [--area A] [--owner O]
            [--bv N --tc N --rroe N --size N]
            [--description-file body.md] [--acceptance-criteria-file ac.md]
backlog edit <id> [--title ...] [--area ...] [--owner ...] [--type ...]
                  [--description-file new.md] [--acceptance-criteria-file ac.md]
                  [--note-file why.md]
backlog score <id> [--bv N] [--tc N] [--rroe N] [--size N]
backlog note <id> --text-file note.md           # appends to the Activity Log

backlog start <id> [--owner me] [--force]       # Backlog -> In Progress
backlog block <id> --reason "..." | backlog unblock <id> | backlog review <id>
backlog archive <id> --as done|cancelled|duplicate [--note "..."]
backlog restore <id>                            # Archive -> Backlog

backlog doctor | backlog reindex                # health check / repair
backlog help [<verb>]                           # the surface, or one verb's options
```

Every prose option — `description`, `acceptance-criteria`, `note`, `text`, `reason` — takes three
spellings: `--<name> "..."`, `--<name>-file <path>`, and `--<name> -` for a pipe. Prefer the file;
see "Giving prose to the CLI" below.

`backlog help <verb>` prints that verb's options and what each is for. It is generated from the
same table the parser checks against, so it is never out of date — reach for it rather than
guessing at a flag, and ask about one verb rather than reading the whole surface.

### If there is no `backlog` on this machine

The same verbs are served over MCP, as one tool taking a verb and an options object:
`{"verb": "show", "options": {"id": "NG-0007"}}`. Everything else in this skill applies there
unchanged — the model, reading cheaply, searching before filing, how a ticket is written — with one
exception: the three spellings of a prose option exist only because a shell damages a quoted value,
and over MCP prose is an ordinary string.

Do not carry that surface in your head; the tool discloses it. `{"verb": "help"}` writes the whole
thing, `{"verb": "help", "options": {"verb": "new"}}` writes one verb and what each option is for,
and the files under `references/` are served there as resources and as
`{"verb": "help", "options": {"topic": "wsjf"}}`. `login`, `logout`, `init` and `install-skill` are
not offered and say why when called.

## Read cheaply

Output is large and you pay for all of it — the same answer differs by more than 10x depending on
how you ask.

- **Answer the question asked.** `next` and `wip` cost ~20 tokens; `list` ~1,300 and `list --json`
  ~4,300. "What should I work on?" is `next`.
- **Cap the queue.** `list --top 10` is a fifth of a bare `list`.
- **Human output is the default** — the same facts as `--json` at a quarter of the size. Reach for
  `--json` only for a field the table omits, then narrow it with `--fields id,wsjf,title`.
- **Finding a ticket is `find`, never `list`.** `list` misses everything in flight or archived and
  cannot see prose at all.
- **`show` before an edit is `show --section description`** (or `acceptance-criteria`, `notes`,
  `activity-log`, or any heading the document has). One section instead of a whole document.
- **`show` trims the Activity Log** to the last few entries; `--full` prints all of it and rarely
  earns its cost.

## Search before you file

`backlog new` will happily file the ticket that already exists. **Run `find` first** whenever you
are about to file something, or are asked whether the backlog covers a topic. It is the only verb
that spans all three tabs and the only one that reads prose.

Every hit says which of two sources matched:

- **`name`** — id, title, area or owner, from the index. Exact and immediate, so a ticket filed a
  minute ago is found by its title.
- **`body`** — the document text, from a full-text index of it. The only route to a description or
  acceptance criteria, with three limits: it matches whole words (`limit` misses *limiting*), it
  may not yet know about a document written minutes ago, and it searches the whole document
  including the Activity Log.

An empty `find` means "no ticket names this and no indexed document contains this word", not "no
such ticket" — retry with the distinctive noun alone, and say what you searched for. Quote the
search text; it is one positional argument, and a split exits 2.

## Do not verify writes

**Exit 0 is the confirmation.** Do not `show` the ticket again to check, and do not run `doctor`
after a write; `doctor` is for suspected damage, not reassurance. A rate-limited request is
*rejected*, never partly applied, so a command that fails with `rate-limited` wrote nothing.

## Times

Human output renders in the configured timezone; `--utc` opts out. **`--json` is always UTC.**

Ticket documents carry **no timestamps, phase, or work state** — those live in the Sheet and,
narratively, in the Activity Log. If asked when something started, use `backlog show <id>`.

## The ticket document

An `# <ID> — <Title>` heading, then `- **Key:** value` bullets, then the prose sections. The
heading is the only home of the id and title.

**The CLI writes the heading and the bullets; below them it touches three things.**
`--description` replaces `## Description`, `--acceptance-criteria` replaces
`## Acceptance Criteria`, and `note` appends to the Activity Log. Everything else — `## Notes`,
anything a person added — is theirs: give them the document URL from `show` to edit in Docs.

Both prose options **replace** the whole section, so read it first and pass the whole new version:

```
backlog show NG-12 --section description
backlog edit NG-12 --description-file new.md --note "why it changed"
```

Passing `""` is refused rather than treated as "empty it". An edit writes no Activity Log entry on
its own — `--note` records one in the same write.

### Write it so a stranger can read it

A ticket is read months later by somebody with none of this conversation and often none of the
codebase. Name the command, the file and the exit code in full. Active voice, third person, one
idea per paragraph. Spell in US English — but a flag keeps its own spelling, so
`archive --as cancelled` is the wire value, not a typo.

Write a concrete actor doing a concrete thing, never `[Noun] + [Hype Verb] + [Abstract Concept]`.
`Improve error handling robustness` names no actor and cannot be scored; `backlog new exits 0
after the shell truncates a description` can be checked. Every claim carries the thing behind it —
the file, the exit code, the count you observed — or it goes.

**This governs prose you author, never prose somebody else wrote:** both options replace the whole
section, so a pass to restyle old tickets deletes an author's words. Read
`references/writing-style.md` before writing a title, description, criteria or note.

### Acceptance criteria are yours to write, not to leave as *TODO*

A ticket filed without them says `- [ ] *TODO*`, which reads downstream as ready when nobody has
said what "done" means. **Write them** — a checklist, one checkable condition per line:

```
- [ ] `backlog doctor` reports the duplicate row and exits 1
- [ ] the row survives a restart
- [ ] README documents the new flag
```

If you genuinely cannot tell what "done" means, file the ticket anyway rather than blocking, then
**say in your reply that the criteria are still `*TODO*` and what you would need to write them**
(`new` prints that on stderr too). Never leave the placeholder silently and report the ticket as
filed. The same goes for the description: file with one, or say that you did not.

### Giving prose to the CLI

**Pass anything longer than a line as `--<name>-file <path>`, not inline** — a checklist almost
always qualifies. `--<name> -` reads stdin, and only one option per command may. The reason is the
shell: PowerShell splits an argument at an embedded double quote, and the CLI refuses the split
value with exit 2 rather than filing a truncated ticket.

**Headings inside a section start at `###`.** A section ends at the next heading of its own level
or higher, so a `##` is a sibling of `## Description` rather than part of it. The CLI refuses one
and names the heading.

`references/prose-input.md` has the rest: empty input, values beginning with two dashes, one stdin
reader per command, and why the `###` rule is a refusal rather than a fix-up.

## Rules

1. **Archive, never delete.** If asked to delete a ticket, archive it as `cancelled` or
   `duplicate` instead.

2. **Go through the CLI.** The Sheet owns the `Cost of Delay`, `WSJF` and `Rank` formulas, and
   writing those cells by hand breaks the index. Editing a ticket *document* by hand is fine and
   expected — `reindex` folds it back in.

3. **Only unstarted work is ranked.** `score` is refused on a started item and its numbers freeze
   as history. If work in flight genuinely should stop, `restore` it to the backlog and say so.

4. **Respect the WIP limit.** `start` refuses to exceed it, and that refusal is the system working.
   Report it and suggest finishing something; use `--force` only if the person asks after being
   told.

5. **Verbs are the transitions.** There is no `--status` flag. Use `start`, `block`, `unblock`,
   `review`, `archive`, `restore`.

## Scoring

WSJF = (business value + time criticality + risk reduction/opportunity enablement) ÷ job size,
each on the modified-Fibonacci scale `1, 2, 3, 5, 8, 13, 20`. Scores are **relative, not
absolute** — the smallest item in each column should be a 1. Read `references/wsjf.md` for what
each dimension means before scoring.

Never invent scores silently. Without enough information to score it, file it unscored (it sorts
last) and say it needs scoring.

## Setup and authentication

Each person signs in as themselves with `backlog login`, which opens a browser once. If a command
fails with `not-signed-in`, tell them to run it — do not authenticate on their behalf, and never
read, copy, print or move anything under the credentials directory.

`Google is rate limiting requests; waiting …` on stderr is normal; it retries on its own. Never
work around a rate limit by editing the Sheet directly.

If no backlog is configured, someone needs to run
`backlog init --drive <sharedDriveId> --timezone <IANA>` once. See the repo README for the
one-time OAuth client setup; do not attempt to create credentials yourself.
