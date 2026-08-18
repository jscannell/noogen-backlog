# How to word a ticket

Read this before writing a title, a description, a set of acceptance criteria, or a note. It covers
*what* to write; `prose-input.md` covers how to get the text into the CLI without the shell
damaging it.

Everything here applies to prose **you** author. It is not license to rewrite somebody else's — see
"What this does not govern" at the end.

## Who reads a ticket

Somebody months from now, with none of this conversation and often none of the codebase. They have
the ticket and nothing else.

So name things in full the first time: the command, the file, the exit code, the symbol. A reader
who has to open the repository to understand the first sentence has already been failed by it.
Three phrases that always fail that reader:

| never write | because |
|---|---|
| "as discussed" | the discussion is not in the ticket |
| "the usual place" | the reader does not know which place |
| "fix the bug in the parser" | there is more than one parser and more than one bug |

## Voice

**Active voice, short sentences, third person.** No `I` and no `we` — a ticket outlives whoever
filed it, and "we decided" names nobody. Write `backlog new files the ticket`, not `the ticket is
filed by backlog new`.

**One idea per paragraph, three sentences at most.** A fourth sentence is a second paragraph or a
second ticket. Vary the length: three sentences of the same shape read as machine output whatever
they say.

**One name per thing.** Pick the word the code and the UI use, then use only that word. `the Sheet`
and `the index` and `the spreadsheet` in one description read as three components.

## US English

Spell in US English: `behavior`, `organization`, `normalize`, `analyze`, `license`. Not
`behaviour`, `organisation`, `normalise`, `analyse`, `licence`.

The reason is search, not taste. Drive's index matches whole words, so a ticket written
`organisation` is invisible to `find "organization"` — the search comes back empty and somebody
files the duplicate. One backlog, one spelling.

**Never respell something the system owns.** `archive --as cancelled` keeps its two `l`s: the wire
value comes from `Outcome.Cancelled`, and it is what the Sheet's Status column and `--json` carry.
Quote a command, a flag, a status or an identifier exactly as it is spelled, whichever English that
happens to be. Correcting one produces a ticket the tool no longer matches.

## Concrete actors, not hype phrasing

Never write `[Noun/Gerund] + [Hype Verb] + [Abstract Concept]`. "Leveraging retries improves
resilience" names no actor, no action and no observable result, so nobody can tell whether it is
true or whether it is done. Write who does what, and what changes.

| not this | this |
|---|---|
| Improve error handling robustness | `backlog new` exits 0 after PowerShell truncates a description |
| Enhance the search experience | `find` misses *limiting* when the search text is `limit` |
| Optimize document write performance | `edit` reads the whole document to replace one section |
| Streamline the onboarding flow | `backlog login` fails with `not-signed-in` and prints no next step |

The left column also hides the size of the work. The right column can be scored.

## Cut the opener

Start with the subject. Never open a sentence with "It is important to note", "Furthermore", "In
order to", or "Moreover" — each one costs a line and carries no fact. "In order to prevent the
duplicate, `doctor` reports it" is "`doctor` reports the duplicate".

## Name the evidence, or drop the claim

Every claim carries the concrete thing behind it: the file, the command, the exit code, the count
you observed, the line from the log. A claim with nothing behind it cannot be checked, and an
unhappy reader cannot tell a measurement from a guess.

- **Not** "this happens frequently" — **write** "three of the last five `new` commands filed a
  truncated description".
- **Not** "performance is poor" — **write** "`list` reads 4,300 tokens where `next` reads 20".

If you cannot name the evidence, delete the claim. Do **not** leave a bracketed placeholder for
somebody to fill in: a placeholder in a filed ticket is the `*TODO*` failure again, and it reads
downstream as a fact. When the evidence exists but you do not have it, file the ticket without the
claim and say in your reply what you would need to make it.

## Tone

Clinical and skeptical. Do not build tension, do not cheerlead, and do not close on an optimistic
summary — "this will greatly improve the developer experience" is a sentence nobody can act on or
disprove.

End on the last fact, the recommendation, or the risk. A description does not need a conclusion,
and restating what it just said wastes the reader twice.

## The fields

### Title

A short factual clause naming the component and the change. No trailing period, no hype verb.

A bug's title states the **wrong behavior**, not the proposed fix — the fix often changes, and a
title that promises one dates badly. `doctor exits 0 on a duplicated row`, not `make doctor stricter`.

Keep it tight. The title is stored whole, but the Drive filename derived from it is cut at 50
characters, so a long title survives and reads worse.

### Description

Three things, in this order:

1. **What happens, or what is needed** — the observed behavior, stated plainly.
2. **Where** — the command, verb, file or component, named exactly.
3. **Why it matters** — the consequence for somebody, not an abstraction.

Once it is long enough to need structure, use `### Problem` and `### Approach`. Headings inside a
section must start at `###`; a `##` is refused, and `prose-input.md` explains why.

Leave out what the reader can get from the ticket itself: no restatement of the title, no repeat of
the type, area or owner, and no timestamps — the Sheet and the Activity Log own those.

### Acceptance criteria

A markdown checklist, one line per condition, each one an **observable outcome** somebody could
check without asking what you meant.

State the outcome, not the activity. `test the doctor command` names work; `doctor exits 1 and names
the duplicated row` names a result you can look at and agree about. Third person, present tense, one
condition per line — a line holding "and" is usually two lines.

### Activity Log prose

`note --text`, `edit --note`, `block --reason` and `archive --note` all land in the log, which is
the only prose record of a ticket's life. One or two past-tense sentences: what happened, and what
it means for the ticket.

A `block --reason` has one extra job — name what is blocking and what would unblock it. "Blocked"
without either leaves the next reader to rediscover it.

## What this does not govern

**Prose a human already wrote.** `--description` and `--acceptance-criteria` replace the whole
section, so a well-meant pass to bring old tickets into this style deletes an author's words and
Docs' revision history is the only way back. Edit an existing section when you are asked to, and
then for its content — never to restyle it.

**Your reply to the person.** This is the register for the document, not for the conversation.
