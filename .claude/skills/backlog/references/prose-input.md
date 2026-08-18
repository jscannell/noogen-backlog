# Giving prose to the CLI

Read this when `backlog new` or `backlog edit` refused something you passed, or when you are about
to pass a description or a set of acceptance criteria and want the rules rather than the summary.

## The short version

| you have | use |
|---|---|
| text longer than a line | `--<name>-file <path>` |
| text already in a pipe | `--<name> -` (only one option per command may read stdin) |
| a single short line, no quotes | inline is fine |

The prose options are `--description`, `--acceptance-criteria`, `--note`, `--text` and `--reason`,
and **each of them answers to all three spellings**. `--note-file` and `--text-file` are not
afterthoughts: a note saying why an edit happened is often the longest thing typed at a backlog
command, and it is the one most likely to quote somebody.

## Why a file rather than the command line

The reason is the shell, not the tool.

Windows hands a native process one command-line string, and PowerShell does not escape a double
quote inside an argument it has already quoted. So a description containing one is split, and
everything after the first quote arrives as separate arguments. The command used to run anyway:
the verb read `Positionals[0]`, nothing looked at the rest, and the ticket was filed with the text
truncated at the quote — exit 0, a confirmation message, and silent data loss.

That is refused now. Every verb rejects any positional argument beyond a ticket id (exit 2, kind
`usage`), and the message names the fragment and explains that the shell split the value. But the
ticket still does not get filed, so the fix is not to retry with different quoting — it is to stop
putting prose on the command line at all.

A file path is one argument whatever the file contains, so it cannot be split. That is the whole
of it.

## The rules the CLI enforces

- **Empty is refused**, on every path — an empty file, an empty pipe, an empty string. Blanking a
  section would throw away writing, and there is no CLI way back; Docs' own revision history is the
  only recovery. To genuinely empty a section, edit the document.
- **One section, one spelling.** Giving the same section both its flags at once
  (`--description` *and* `--description-file`) is an error rather than a silent precedence rule.
- **One stdin reader.** There is one pipe, so only one option per command may read `-`. The second
  reader would see it as empty and report the wrong problem. When both sections are long, give at
  least one of them as a file.
- **An option that takes a value must be given one.** A bare `--title` is an error, not "leave the
  title alone", and an option will not swallow another option as its value —
  `note NG-12 --text --json` errors instead of writing a note that says `--json`.
- **A value that begins with two dashes** is joined with `=`: `--title=--draft`.
- **An option a verb does not read is a usage error**, so a flag borrowed from another verb fails
  loudly rather than being ignored.

## What a section body may hold, and where a section ends

A section runs from its `## ` heading to the **next heading of the same level or higher**. That is
one rule, and both the reader (`show --section`) and the writer (`--description`) use it, so what
you read back is exactly what a replacement overwrites.

The consequence: **headings inside a description or a set of acceptance criteria must start at
`###`.** A `##` heading is a sibling of `## Description`, not a part of it, so a body that opens
with one falls outside the section it was written into.

```
## Problem          <- refused: a sibling of `## Description`, not part of it
### Problem         <- fine, and what you meant anyway
```

This is refused rather than accepted, because accepting it was silent damage. The *first* write of
such a body looked right — the text was all there, under the right heading. But the section
boundary now fell at the body's own first heading, so the region the next write could reach was
empty: it had nothing to remove, inserted instead, and left the previous body stranded below,
out of reach of every write after that. The copies accumulated, one per edit, and nothing reported
it — `doctor` compares the Sheet against the document's metadata, and the metadata stayed correct
throughout. `show --section description` came back empty, which read as "this ticket has no
description".

The CLI cannot tell such a heading apart from a section a person added, because they are the same
thing on the page. Bounding the section by the headings we know instead would resolve that the
other way and delete the person's section, which is worse.

Two smaller notes:

- The refusal is exit 1, kind `invalid-argument`, and the message names the heading and the `###`
  spelling to use instead. It happens before anything is written, so a refused `new` files no
  ticket and a refused `edit` leaves the document untouched.
- `backlog doctor` reports a document that holds the same `## ` heading more than once, kind
  `duplicate-section`. That is the shape the old fault left behind, and the stale copies can only
  be removed in Docs.

## Encoding

Files and pipes are decoded as UTF-8 after a byte-order mark, falling back to the console encoding
rather than emitting `U+FFFD`. You do not need to do anything about this; it is here so that seeing
mangled characters points you at the file's encoding rather than at the tool.

## Why acceptance criteria have a flag at all

Leaving them off did not make them a human's job — it made them nobody's. With no CLI path to
them, every ticket an agent filed kept `- [ ] *TODO*`, and every reader downstream was told the
ticket was ready when nobody had said what "done" meant.

Both `--description` and `--acceptance-criteria` still seed `*TODO*` when nothing is given, because
filing fast is worth keeping. What must not happen is a *silent* placeholder: `backlog new` names
the sections it left unwritten on stderr, and you should repeat that in your reply. See the
"Acceptance criteria are yours to write" section of `SKILL.md`.
