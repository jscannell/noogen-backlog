# Noogen backlog

A lightweight, company-wide work-item tracker: **one Google Doc per ticket in a Drive shared drive,
indexed by a Google Sheet, prioritized with WSJF, run as Kanban.** Readable and editable by humans
in Docs; queryable and writable by agents through the `backlog` CLI or the MCP server that
serves the same verbs.

Completed work is **archived, never deleted**.

## Why a CLI

The Drive MCP connector is read-and-create only — no update, no move, no delete, and no Sheets
API at all. An agent could file a ticket through it but never edit one, never archive one, and
never touch the index. So access goes through this first-party CLI, backed by a service account.

A CLI needs three things per machine, though — install the tool, install the skill, sign in — so a
client that is not a shell on that machine cannot reach the backlog at all. [The MCP
server](#the-mcp-server) serves the same verbs to those callers. It is a second way in, not a second
implementation: both front ends sit on one `BacklogApi` and emit one set of shapes.

## The model

Work moves through three Kanban columns, and **the tab a ticket lives on is its state**:

| tab | phase | ranked? | holds |
|---|---|---|---|
| `Backlog` | not started | **yes — the WSJF queue** | everything unstarted; unscored sorts last |
| `In Progress` | pulled, not finished | no | `State` ∈ `in-progress`, `in-review`, `blocked` |
| `Archive` | terminal | no | `Outcome` ∈ `done`, `cancelled`, `duplicate` |

Because the tab is the state, "is this ranked?" is a property of *where the row lives* rather than
a flag anyone has to maintain. `Cost of Delay`, `WSJF`, and `Rank` are live Sheet formulas on
`Backlog` only; once work starts the scores freeze as static values — a historical record for
calibrating estimates, never recomputed.

### The columns

The Sheet is meant to be read and edited by hand, so the headers are words rather than
abbreviations. `WSJF` keeps its name — it is the term of art.

| column | what it is |
|---|---|
| `ID` | the ticket id, e.g. `NG-0007` |
| `Title` | links to the ticket document |
| `Type`, `Area`, `Owner` | classification |
| `Business Value` | what the organization or its users gain |
| `Time Criticality` | how fast the value decays |
| `Risk & Opportunity` | risk reduction and opportunity enablement |
| `Job Size` | effort, the denominator |
| `Cost of Delay` | `Business Value + Time Criticality + Risk & Opportunity` |
| `WSJF` | `Cost of Delay / Job Size` |
| `Rank` | queue position, `Backlog` only |
| `State`, `Blocked Reason`, `Blocked At`, `Started At` | `In Progress` only |
| `Outcome`, `Archived At`, `Lead Time (days)`, `Cycle Time (days)` | `Archive` only |
| `Created`, `Updated` | datetime cells; see [Times](#times) |
| `Drive File ID`, `Drive File Link` | hidden. Drive's own handle for the ticket document, and a cached link so the `Title` cell can be hyperlinked without a Drive round-trip |

Columns resolve by header name, never by position, so reordering or adding your own is safe. A
backlog created before the columns were spelled out still answers to the short names it has —
`bv`, `tc`, `rroe`, `size`, `cod`, `lead_days`, `cycle_days`, `doc_id`, `doc_url` — and nothing
relabels an existing header row. Ticket documents use the same name resolution, so a field you
label `job size` or `bv` by hand is understood; unlike the header row, a document is rewritten
with the spelled-out names on its next save.

Drive layout:

```
Noogen Backlog/                     (shared drive)
├── Backlog Index                   Google Sheet — Backlog / In Progress / Archive / Config
├── tickets/
│   └── NG-0007-wsjf-index-tool     Google Doc
└── archive/2026/Q3/
    └── NG-0003-slack-thread-replay Google Doc
```

The `Title` cell in the Sheet links to the ticket document. It is a rich-text link rather than a
`HYPERLINK()` formula, so the stored cell value stays the plain title and ordinary reads are
unaffected.

## The ticket document

Markdown is the format; a **Google Doc** is the file. The CLI uploads markdown and Drive converts
it on the way in, so clicking a title in the Sheet opens the Docs editor with the ticket rendered —
headings as headings, fields as a bullet list — rather than a Drive preview of raw text. Reads go
the other way, exporting the Doc back to markdown.

The cost of that is worth knowing: Docs stores paragraphs and lists, not markdown, so an export is
Docs' rendering of the document in its own style rather than a byte-for-byte copy of what was
written. Hard-wrapped lines come back as one paragraph, `_italic_` as `*italic*`, and some
punctuation backslash-escaped. That drift is stable, not cumulative — it happens on the first save
and then stops. What the CLI generates is already written in Docs' style, so a new ticket's body
comes back as it went in apart from a backslash on the timezone offset in the Activity Log — an
export artifact that never appears in the document a person reads. The CLI rewrites the heading
and the bullet block on every save, so the fields
always come back to the canonical form; the prose below them is copied through exactly as Docs
returned it, never "cleaned up".

A heading, the fields a person edits, then the prose:

```markdown
# NG-0007 — WSJF index tool

- **Type:** feature
- **Area:** agent
- **Owner:** jason
- **Business Value:** 8
- **Time Criticality:** 3
- **Risk & Opportunity:** 2
- **Job Size:** 5

## Description

…

## Acceptance Criteria

- [ ] …

## Notes

## Activity Log

- 2026-08-01 09:00 -04:00 — created
```

Every part of it renders. Docs is where someone who does not use the CLI reads a ticket, and a
`---` frontmatter block does not survive the conversion the way it would on a code host — it showed
up as literal text or vanished into a horizontal rule, so the first thing the reader saw was
plumbing.

**The heading is the only home of the id and the title.** They used to sit in frontmatter *and* in
a heading that nothing rewrote, so `backlog edit --title` left the heading stale permanently and no
check could see it: `doctor` compares the Sheet against the metadata, and those two agreed.

Everything below the bullets is yours. The CLI regenerates the heading and the bullet block on
every write and copies the rest through untouched — including a sentence typed straight under the
title, which is body, not a field. An unrecognized bullet round-trips rather than being eaten, so
`- **epic:** platform-rebrand` survives.

Editing a document by hand is expected; `backlog reindex` folds those edits back into the Sheet.
What is deliberately *not* here is timestamps, phase, and work state — see
[the invariants](CLAUDE.md).

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
on metrics reported in days. And serials are quantized to whole seconds, since a fractional day in
a double cannot represent most instants exactly.

## Install

Given the `.nupkg` (see [Distributing](#distributing) for where it comes from):

```bash
dotnet tool install -g --add-source <folder-holding-the-nupkg> Noogen.Backlog.Cli
backlog install-skill      # writes the Claude Code skill into ~/.claude/skills
backlog login              # opens a browser, once
```

Installing globally is deliberate — the backlog spans repos, so the tool should not live inside
any one of them.

`install-skill` unpacks the skill the tool carries. Pass `--path <dir>` to install it somewhere
else — a project's `.claude/skills`, say. It refuses if what is already there differs from what
the tool carries, listing the files, and `--force` replaces them; nothing under `~/.claude` gets
overwritten without you saying so. Claude Code loads the skill in the next session you start.

## Distributing

One artifact carries both halves. The build embeds the skill from `.claude/skills/backlog` into
the CLI assembly, so the `.nupkg` holds the binary *and* the skill, and nobody can end up with one
updated and the other stale.

```powershell
./scripts/deploy.ps1                  # test, pack, install globally, install the skill
./scripts/deploy.ps1 -Version 1.2.0   # the same, but a version worth handing to someone
```

That is the loop after a change: run it, and this machine is running what you just built. It
leaves the `.nupkg` in `artifacts/` and prints the two commands a recipient runs.

Every run stamps a unique version — a dev timestamp unless you pass `-Version`. Without that,
NuGet would serve the cached `1.0.0` from `~/.nuget/packages` and install the *previous* build,
which defeats the purpose of packing at all.

To pack without installing anything:

```bash
dotnet pack src/Noogen.Backlog.Cli/Noogen.Backlog.Cli.csproj -c Release
```

Two things are baked in at build time and neither is required: the
[OAuth client](#the-oauth-client-oauthjson) and the skill. `BacklogSkillDirectory` overrides where the skill is read from, the same way
`BacklogOAuthClientFile` overrides the client.

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

### The OAuth client (`oauth.json`)

**What it is.** The credential that identifies *this CLI* to Google — not any person, and not any
grant of access. It tells Google's consent screen which application is asking. Everyone in the
organization uses the same one; individual identity comes from `backlog login`.

**Which console?** You create it in **Google Cloud** (`console.cloud.google.com`), not in Workspace
admin. Workspace matters in exactly two ways, both covered below: it is what makes the *Internal*
option available, and it is where an admin can allowlist or revoke the app afterwards.

This is **not** domain-wide delegation. The Gmail integration in the platform repo uses DWD, set up
in `admin.google.com`, where a service account is authorized to impersonate users. Nothing like
that is needed here, and there is no reason to visit that page for this.

**Prerequisite: the Cloud project must belong to your Workspace organization.** Open the project
picker in the Cloud console and check the **Organization** column shows your domain rather than
"No organization". If the project was created under a personal account, the *Internal* user type
is simply not offered, and you would be pushed toward External and Google's verification review.
Move the project into the org, or use one that already lives there.

**Created once, by one person, for everyone.**

1. Cloud console → **APIs & Services → OAuth consent screen** (newer consoles: **Google Auth
   Platform**)
   - User Type / Audience: **Internal**. This is the consequential choice: internal apps are
     limited to your Workspace domain and skip Google's verification review entirely. `drive` is a
     *restricted* scope, so an External app would face a lengthy security assessment before anyone
     could use it.
   - Add the scopes `drive`, `spreadsheets`, `openid`, `email`.
2. → **Credentials → Create Credentials → OAuth client ID**
   - Application type: **Desktop app**. Not "Web application" — a web client cannot drive the
     loopback redirect an installed app uses. (If you pick wrong, `backlog login` says so by name.)
3. **Download JSON** and save it to `%APPDATA%\Noogen\oauth.json` — unedited, exactly as Google
   gives it to you. On macOS and Linux that path is `~/.config/Noogen/oauth.json`.
4. Use the **same GCP project** that has the Drive API and Sheets API enabled. The client in use
   today lives in project **`backlog-504721`**, so those two APIs must be enabled there — not in
   whichever project the rest of the platform uses.

**Where Workspace admin does come in.** If your org restricts third-party app access
(Admin console → Security → Access and data control → **API controls → App access control**), an
admin may need to mark this client ID as **Trusted** before anyone can consent — strict orgs block
unconfigured apps by default. That same page is where you revoke the app org-wide if a machine is
ever compromised.

**What the file looks like.** Google's download:

```json
{
  "installed": {
    "client_id": "123456789012-abc...xyz.apps.googleusercontent.com",
    "project_id": "astral-net-346914",
    "auth_uri": "https://accounts.google.com/o/oauth2/auth",
    "token_uri": "https://oauth2.googleapis.com/token",
    "auth_provider_x509_cert_url": "https://www.googleapis.com/oauth2/v1/certs",
    "client_secret": "GOCSPX-...",
    "redirect_uris": ["http://localhost"]
  }
}
```

Only `installed.client_id` and `installed.client_secret` are read; the rest is ignored. A minimal
hand-written alternative also works:

```json
{ "clientId": "123...apps.googleusercontent.com", "clientSecret": "GOCSPX-..." }
```

**Distributing it to the team — it is built into the tool.** Put `oauth.json` at the repository
root and the build embeds it, so an ordinary install needs nothing on disk and nobody but the
person who packs the tool ever handles the file:

```bash
# once, on the machine that packs the tool
cp ~/Downloads/client_secret_*.json oauth.json
dotnet pack src/Noogen.Backlog.Cli/Noogen.Backlog.Cli.csproj -c Release
```

The build prints which it did — `Embedding OAuth client from …` or `No OAuth client at …`.
`backlog whoami` then reports the client id and where it came from, so the result is verifiable
rather than assumed.

Building **without** the file is fully supported: the tool falls back to
`%APPDATA%\Noogen\oauth.json` and then to `NOOGEN_BACKLOG_OAUTH_CLIENT_ID` /
`NOOGEN_BACKLOG_OAUTH_CLIENT_SECRET`. That is what a contributor who does not have the secret
gets, and the whole test suite passes in that state.

Resolution order is environment → local file → embedded. The embedded copy is the organization
default, and it comes last so anyone testing against a different client can override it without
rebuilding.

In CI, write the secret to a temporary path and point the build at it:

```bash
dotnet pack ... -p:BacklogOAuthClientFile=$RUNNER_TEMP/oauth.json
```

⚠️ **`oauth.json` is gitignored and must stay that way.** Not because it is dangerous — see
below — but because GitHub's secret scanning detects the `GOCSPX-` prefix and would alert, or
block the push, every time.

**Is the client secret actually secret?** No, and Google says so explicitly: for installed
applications the client secret cannot be kept confidential, because it ships to every user's
machine. Security rests on the user's own consent plus the loopback redirect, and the real access
boundary is each person's Google account and their shared-drive membership. Someone holding this
file can present a consent screen that says "Noogen backlog" — they cannot read your Drive.
Treat it as low-sensitivity configuration, not as a credential.

**When something is wrong**, `backlog login` names the specific problem rather than repeating the
setup guide — wrong client type, malformed JSON, missing `client_secret`, or unrecognized shape.

## One-time Google setup

1. Enable the **Drive API** and **Sheets API** on the GCP project.
2. Create the OAuth client described above — once, for the whole organization.
3. Make sure everyone who needs the backlog is a member of the shared drive. That, not any grant
   inside this tool, is what controls access.
4. Each person runs `backlog login`.
5. `backlog init --drive <sharedDriveId> [--timezone America/New_York]` — creates the folders,
   the index, the tabs, the formulas, and the Config tab, and stamps the timezone onto the
   spreadsheet. It is idempotent, so re-running repairs a half-finished setup. The timezone
   defaults to this machine's on first run. The tabs are left in flow order —
   **Backlog, In Progress, Archive, Config** — and the default tab a new spreadsheet arrives
   with is renamed into the Backlog tab rather than left behind empty.

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
backlog find "<text>" [--area A] [--top N]      # search every tab, names and prose
backlog show <id> [--section S] [--full]        # one ticket and its body
backlog flow [--since 90d]                      # throughput, cycle-time p50/p85

# list, next, wip and find take --fields to narrow the --json projection:
#   backlog list --top 10 --json --fields id,wsjf,title

backlog new --title "..." [--type feature|bug|chore|spike] [--area A] [--owner O]
            [--bv N --tc N --rroe N --size N]
            [--description "..."] [--acceptance-criteria "..."]
backlog edit <id> [--title ...] [--area ...] [--owner ...] [--type ...]
                  [--description "..."] [--acceptance-criteria "..."] [--note "..."]
backlog score <id> [--bv N] [--tc N] [--rroe N] [--size N]
backlog note <id> --text "..."

# every prose option is safer given as a file or a pipe than as an argument, at any length.
# --description, --acceptance-criteria, --note, --text and --reason each also answer to
# --<name>-file <path>, and to --<name> - for a pipe:
#   backlog new --title "..." --description-file body.md --acceptance-criteria-file ac.md
#   backlog edit <id> --description-file new.md --note-file why.md
#   Get-Content body.md -Raw | backlog new --title "..." --description -

# the score flags are also spelled out, for anyone who prefers them:
#   --business-value  --time-criticality  --risk-opportunity  --job-size

backlog start <id> [--owner me] [--force]
backlog block <id> --reason "..." | backlog unblock <id> | backlog review <id>
backlog archive <id> --as done|cancelled|duplicate [--note "..."]
backlog restore <id>

backlog init --drive <sharedDriveId> [--timezone America/New_York]
backlog install-skill [--path DIR] [--force]    # the Claude Code skill, into ~/.claude/skills
backlog doctor | backlog reindex

backlog help                                    # every verb, grouped
backlog help <verb>                             # one verb: usage, and what each option is for
```

Every command accepts `--json`. That flag is the machine contract, and it is what makes the tool
usable by agents: the skill teaches the CLI surface, not the storage layout.

Both help texts are generated from `VerbCatalog`, the same table the parser checks a command line
against. So the help cannot name a flag no verb reads, and cannot miss one that was added. Ask for
one verb by name rather than reading all of it — that is the cheaper question, and it is the whole
answer.

### Searching

`find` is the one query verb that spans all three tabs, and the one that reads prose. It exists for
the question asked before filing anything — *have we already got a ticket about this?* — which
`list` answers badly and expensively, and which nothing answered at all for work in flight or
already archived.

It reads two sources, and says on every hit which one matched:

```
backlog find "rate limit"

id       match      phase        wsjf  area      owner        title
-------  ---------  -----------  ----  --------  -----------  ----------------------------
NG-0031  name+body  archive      6     platform  j@noogen.ai  Retry a rate-limited request
NG-0070  body       in-progress  3.33  platform  j@noogen.ai  Search the backlog
```

**Names** — id, title, area, owner — come from the Sheet, matched as **substrings**, case
insensitively. That half is exact and immediate, which is what makes it the half a duplicate check
can rely on.

**Body** text comes from Drive's full-text index, because the Sheet holds no prose at all: the
description and the acceptance criteria live only in the document. Three properties of that index
are worth knowing before you trust a `find` that returned nothing:

- It matches **whole terms, not substrings**. `find limit` will not match *limiting*, and `auth`
  will not match *oauth*. The name half has no such restriction, which is why both exist.
- It is **eventually consistent**. A document written in the last few minutes may not be indexed
  yet, with no read-your-writes guarantee — which is exactly the moment a duplicate check runs. The
  name half covers it, so a ticket filed a minute ago is still found by its title.
- It indexes the **whole document**, Activity Log included. A ticket whose log says
  `blocked — waiting on the rate limiter` matches a search for the rate limiter.

The two halves are joined on `Drive File ID`, the handle the Sheet already keeps for each document.
Joining there rather than on the file's name is what keeps the answer authoritative: every row
returned is a Sheet row, so phase and rank are right, and a Drive hit on something that is not a
ticket — the index spreadsheet, a document somebody dropped into the folder — falls out of the join
instead of being reported as a result.

The Drive query is confined to the backlog's own shared drive with `corpora=drive`, resolved once
from the tickets folder. The tool holds full Drive scope by design (see **Authentication**), so an
unconfined `fullText` query would sweep everything the signed-in person can read. A backlog rooted
in My Drive has no shared drive to name; there the query is unconfined and the join is what bounds
the result.

Scoping by parent folder would have been the obvious alternative and is wrong: Drive's
`'<id>' in parents` matches immediate children only, and archived documents sit two levels down
under year and quarter, so a parent-scoped query would quietly answer for active tickets alone.

### Asking for less

An agent pays for every byte it reads back, and the backlog's answers are large: a bare
`list --json` over forty-odd tickets is around 17,000 characters, most of which the question did
not need. Three options narrow it, and the skill teaches all three.

`--fields` narrows the `--json` projection on `list`, `next`, `wip` and `find` to the columns named —
`--fields id,wsjf,title` is a third the size of the whole thing. The names are the ones that appear
on the wire, they are taken from the projection itself rather than a list kept alongside it, and an
unrecognized one is a usage error listing the real ones. Asking for a field a particular ticket
does not carry leaves it absent, exactly as it would be without the option: absent means absent.

On `show`, `--section` does the same to the body — `--section description`, `acceptance-criteria`,
`notes`, `activity-log`, or any heading the document actually has. This is what you want before
rewriting a section, because a prose option replaces the whole thing and you need to know what is
there now. Reading one section is a tenth of reading the document.

And `show` **trims the Activity Log** to its last three entries by default, with a line saying how
many it left out. The log grows by one line for every lifecycle event and never shrinks, so on a
ticket that has been worked for a while it is most of the document — and it is rarely what the
reader came for. `--full` prints all of it. Asking for it by name with `--section activity-log`
also gives it whole; trimming what was explicitly requested would answer a different question.

The trim is **display only**. Nothing on a write path ever sees a shortened body: the log is the
only prose record of a ticket's life, and storing a trimmed copy would destroy history that nothing
else holds. `TicketDocument.TrimActivityLog` is called from the `show` command and nowhere else.

`--json` itself is not indented, for the same reason — the whitespace was a quarter of a list
response and was never part of what the shapes promise. Pipe it through a formatter to read one by
hand.

Verbs *are* the transitions — there is deliberately no `--status` flag to pass an illegal value
to, and `edit --status` errors with a pointer to the right verb.

Each verb declares the options it reads, and an option it does not read is a **usage error**
(exit 2) rather than something quietly ignored. A dropped flag is worse than a rejected one: the
command goes on to succeed at doing nothing, and reports it. Where the flag is one people
reasonably reach for, the error says what to do instead rather than only listing what is accepted.

The declaration includes each option's *shape*, so the parser never guesses one. A valueless flag
— `--json`, `--utc`, `--force` — does not consume the argument after it; an option that takes a
value and is given none is a usage error naming itself, rather than turning into a flag nobody
reads and leaving the field alone; and the argument after an option is its value even when it
begins with two dashes. The one thing an option will not take is another option the same verb
reads: `note NG-12 --text --json` is someone who forgot the text, not a note that says `--json`,
so it errors. If a value really does start with two dashes, write it as `--text=--json`.

The same goes for positional arguments. A verb takes one — a ticket id, or `find`'s search text —
or none, and anything past that is refused, because the usual way an extra one appears is a value
the shell tore in half.
Windows hands a native process one command-line string; PowerShell quotes an argument containing
whitespace but does not escape a double quote already inside it, so
`--description 'he said "no" and left'` ends the quoted run at `"no"` and the rest arrives as
separate arguments. Those used to be dropped: the ticket was written with everything after the
first quote missing, and the command exited 0.

### Giving prose that the shell cannot damage

Inline is fine for a line. For anything longer, or anything containing a quote, use one of the two
paths that never reach the command line:

```powershell
backlog new --title "Rework the importer" --description-file body.md --acceptance-criteria-file ac.md
backlog edit NG-12 --description-file body.md --note-file why.md
Get-Content body.md -Raw | backlog new --title "Rework the importer" --description -
```

This is every option that carries free text, not only the two that write a section:
`--description`, `--acceptance-criteria`, `--note`, `--text` and `--reason`. The shell splits a
note exactly as it splits a description, and the notes worth writing — why an edit happened, what
a ticket is blocked on — are the long ones. `Verbs` derives each `-file` spelling from the option's
own name rather than listing it separately, so a prose option cannot be declared with a flag the
reader never looks at.

A path is a single argument whatever is inside the file it points at, and a pipe is not an
argument at all. Both accept `-` for standard input, both refuse a file or a pipe that turned out
to be empty, and passing `--description` and `--description-file` together is a usage error rather
than one silently winning. Only one option per command may read standard input: there is one pipe,
so the second reader would find it drained and report the text as empty, naming the wrong problem.
That combination is refused up front instead.

Bytes are read as UTF-8 — after a byte-order mark if there is one, which wins outright. If they
are not valid UTF-8 they are decoded with the console's own encoding instead, because PowerShell
5.1 encodes what it pipes to a native process using the OEM code page; reading those bytes as
UTF-8 would replace every em dash and curly quote with `U+FFFD`, which is the same silent
corruption in a different place. Well-formed ASCII decodes identically either way, so the fallback
only runs on input UTF-8 could not have produced.

`--description` and `--acceptance-criteria` replace the document's `## Description` and
`## Acceptance Criteria` sections and nothing else. Those two are the only prose the tool
rewrites — Notes, the Activity Log, and any section someone added come back byte-identical, and a
`###` subheading inside one stays part of it. A document with no such heading gains one rather
than having a section guessed at. Blanking is refused: `--description ""` errors instead of
emptying the section, because Docs' revision history is the only way back from an overwrite.

Because a replacement needs the current text, `show --section description` reads back exactly what
a replacement would overwrite — the same boundary walk answers both, so the two cannot disagree
about where a section ends.

That boundary is also a rule the text has to keep: **a section body's headings start at `###`.** A
`##` is a sibling of `## Description` rather than part of it, so a body that opens with one falls
outside the section it was written into. The write is refused, before anything is written, with a
message naming the heading and the deeper spelling to use. Accepting it used to be silent damage —
the first write looked right, and every write after it inserted a fresh copy above a stale one it
could no longer reach, one per edit, with the index correct throughout. `backlog doctor` now reads
the body for the shape that leaves behind: a `##` heading a document holds more than once, reported
as `duplicate-section`. Stale copies can only be deleted in Docs.

An edit still does not log itself: a log entry saying "description edited" would say nothing Docs'
revision history does not already hold. When the change *is* worth recording, `edit --note "..."`
carries the line in the same write, rather than costing a second command and a second round trip
against the document that was just saved.

Acceptance criteria are a flag rather than something only Docs can supply because leaving them to
Docs meant they never got written. A ticket filed without them carries `- [ ] *TODO*`, and
`backlog new` says so on stderr, naming the command that fills it in — a placeholder nothing
mentions is indistinguishable from a finished ticket.

### When Google says no

Sheets and Drive have per-minute, per-user quotas, and a `doctor` or `reindex` sweep over a large
backlog can reach them. A rate-limited request is **rejected, not half-applied**, so the tool
simply waits and sends it again — up to four times, backing off about 1s, 2s, 4s, 8s, or for
exactly as long as Google's `Retry-After` asks. While it waits it says so on stderr, so a paused
command does not look like a hung one, and `--json` output on stdout stays a single clean document:

```
Google is rate limiting requests; waiting 2.3s before retry 2 of 4.
```

Only refusals that clear on their own are retried. An exhausted *daily* quota, or a permission
error, fails immediately — waiting would not have helped. If the retries run out, the command
exits **4** with kind `rate-limited`; nothing was partly written, so running it again is safe.

Failure kinds, all reported as `{"kind": ..., "error": ...}` under `--json`:

| exit | kind | meaning |
|---|---|---|
| 1 | `wip-limit`, `illegal-transition`, `not-found`, `invalid-argument`, `malformed`, `error` | the command was understood and refused, or something went wrong |
| 2 | `usage` | bad arguments |
| 3 | `not-signed-in`, `oauth-client-missing`, `oauth-client-invalid` | authentication needs a human |
| 4 | `rate-limited` | Google throttled us past the retries; try again shortly |

## The MCP server

`Noogen.Backlog.Mcp` serves the same verbs over MCP, so a caller that is not a shell on the machine
holding the credentials can still reach the backlog. It speaks protocol revision **2026-07-28** over
Streamable HTTP.

### CLI or server

Reach for the CLI when you are a person at a terminal, or an agent that can shell out on a machine
where the tool is installed and someone has signed in. It renders tables, resolves times into the
backlog's timezone, and is the only place `login`, `init` and `install-skill` exist at all.

Reach for the server when none of that is true: a client with no shell, a machine with no `backlog`
on `PATH`, a fleet where three per-machine setup steps is three too many. It also means a change
reaches every caller the moment the server restarts, without redistributing a tool.

The two answer identically. `--json` from the CLI and `structuredContent` from the server are the
same bytes for the same request, because there is one copy of those shapes and both front ends emit
it.

### Running it

Everything is configured through the environment — the CLI's config file is a person's own state
and the server does not read it.

| variable | what it is |
|---|---|
| `NOOGEN_BACKLOG_SPREADSHEET_ID` | **required.** The Backlog Index spreadsheet. Create one with `backlog init` from the CLI |
| `NOOGEN_BACKLOG_CREDENTIALS` | a service-account key file — the ordinary answer for a deployed server |
| `NOOGEN_BACKLOG_OWNER` | who a write is attributed to when the caller names nobody |
| `NOOGEN_BACKLOG_TOKENS` | a directory of tokens written by `backlog login`, for a server run on a workstation that has already signed in |
| `NOOGEN_BACKLOG_OAUTH_FILE` | an OAuth client on disk, needed only to refresh a token from the directory above |
| `NOOGEN_BACKLOG_ACCOUNT` | which stored account to use, when there is more than one |

```bash
export NOOGEN_BACKLOG_SPREADSHEET_ID=1AbC...
export NOOGEN_BACKLOG_CREDENTIALS=/etc/noogen/backlog-sa.json

dotnet run --project src/Noogen.Backlog.Mcp
# listening on http://127.0.0.1:5099
```

Credentials resolve **once, at startup**, in the order [Authentication](#authentication) describes.
A server that cannot reach the backlog says why and exits rather than accepting calls it will fail
one at a time.

It binds loopback by default, and that default is deliberate: this process holds one credential to a
whole company's backlog and authenticates nobody. Putting it on a network is a decision to make on
purpose — `ASPNETCORE_URLS` or `--urls` is how — and per-caller identity and authentication are
NG-0093, not this.

Point a client at `http://127.0.0.1:5099/`. For Claude Code:

```bash
claude mcp add --transport http backlog http://127.0.0.1:5099/
```

### What it offers

One tool, `backlog`, taking a verb and an options object:

```json
{ "verb": "show", "options": { "id": "NG-0007" } }
```

One rather than one per verb, because a 2026-07-28 server's tool list may not vary per connection or
as a side effect of another call — a surface cannot be unlocked after discovery, so whatever a
caller learns, they learn inside a tool result. What is loaded up front is therefore small: the
server's `instructions`, and one tool definition carrying the verb list. Everything below that is
asked for.

`options` carries everything the verb reads, including the argument its usage shows in angle
brackets — `show <id>` is `{"id": "..."}`. Prose is an ordinary JSON string: the CLI's
`--<name>-file` and `--<name> -` spellings exist to survive a shell splitting a quoted value, and
there is no shell here, so they are neither offered nor needed.

Every verb the CLI has is offered except four, each of which says why when it is called:

| withheld | because |
|---|---|
| `login`, `logout` | the browser would open on the server's machine, and there is no credential of yours here to revoke |
| `init` | which backlog this server serves is its configuration, not a tool call |
| `install-skill` | the server cannot reach your `~/.claude`. The same guidance is served as resources |

`json` and `utc` are not options here either. Results are always structured and always UTC.

Three things a caller can ask for rather than carry:

- `{"verb": "help"}` — the whole surface, grouped, generated from the same table the CLI's help is.
- `{"verb": "help", "options": {"verb": "new"}}` — one verb, and what each option is for.
- `{"verb": "help", "options": {"topic": "wsjf"}}` — one of four guides: `overview`,
  `writing-style`, `wsjf`, `prose-input`. These are also MCP resources at
  `backlog://guide/<topic>`, and they are the skill's own bytes — the same file
  `backlog install-skill` writes to disk, not a copy.

A refusal names what the verb actually accepts, at the moment it was got wrong, which is what makes
exploring cheap.

### Two differences worth knowing

**`whoami` answers about the server, not about you, and says as little as it can.** The CLI reports
a person's own setup — which account is signed in, where the token store is, what protects it. None
of that exists here: the caller has no credential on this path and the server's is not theirs. So it
answers with two keys, `owner` and `sharedIdentity`, which is the only part a caller can act on —
every caller shares one identity, so a ticket does not carry your name unless you pass `owner`.

Which credential was chosen and which spreadsheet it opens are deliberately *not* on the wire: the
description names a key file's path on the server's filesystem, or the operator's own address, and
the spreadsheet id is the handle to a whole backlog. Those go to the log at startup, where the
person who deployed it is looking. The two keys that remain are also the two that stay true once
callers have identities of their own (NG-0093): `owner` becomes theirs and `sharedIdentity` becomes
false.

**The text half of a result is a sentence, not the JSON again.** The specification says a structured
result SHOULD also carry its JSON serialized as text, for a client that reads only `content`. Done
literally that doubles every response for a reader who is a model paying by the byte. What that
SHOULD protects is a client that would otherwise get nothing usable, and `Created NG-0042.` serves
that better than a second copy of the record. `structuredContent` is the contract; `content[0]` says
what happened.

A refusal is `isError` on the result, never a JSON-RPC error — the call was well formed and the
backlog said no, which is an answer. Its shape is the CLI's failure shape, `{"kind": ..., "error":
...}`, under the same one-word kind the CLI turns into an exit code.

## WSJF

`WSJF = (Business Value + Time Criticality + Risk & Opportunity) / Job Size`, each on the
modified-Fibonacci scale `1, 2, 3, 5, 8, 13, 20`,
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
| `Noogen.Backlog` | all the logic — store, lifecycle, index, documents, metrics — and the contract every front end emits: `BacklogApi`, the JSON shapes, the name of a failure |
| `Noogen.Backlog.Verbs` | the text-verb surface: the verb and option catalog, the generated help, the embedded skill |
| `Noogen.Backlog.Cli` | thin arg-parsing shell over `BacklogApi`, packaged as a global tool |
| `Noogen.Backlog.Mcp` | ASP.NET Core MCP server over `BacklogApi`: one tool, every verb, Streamable HTTP |
| `Noogen.Backlog.Tests` | xUnit against in-memory Drive/Sheets fakes |
| `Noogen.Backlog.Mcp.Tests` | xUnit over the same fakes, driving the tool directly |
| `Noogen.Providers.GoogleWorkspace.Tests` | xUnit over a stub HTTP transport, so gateway requests are asserted on the wire |

The logic deliberately lives in a library rather than the CLI: the Noogen platform agent is a
future consumer, and adding a `BacklogToolset` there should mean writing `[AgentTool]` wrappers
over `BacklogApi`, not reimplementing any of this. Auth resolving through the ADC chain means
the same code path serves a key file today and Workload Identity in GKE later.

So is everything a caller can *see*, split by the shape a front end takes. `BacklogApi` answers
with `IBacklogView` shapes that `BacklogJson` spells, and `BacklogFault` gives a failure one name
each front end maps into its own vocabulary — the CLI to an exit code, a server to a status. Those
are in `Noogen.Backlog`, because every front end needs them.

`Noogen.Backlog.Verbs` holds what only a *text-driven* front end needs: the verb and option
catalog, and the help generated from it. A REST API is resource-shaped — `POST /tickets/{id}/start`
is built from `BacklogApi` and references the domain and not the verb layer. That reference graph
is how the line stays honest, and `AssemblyBoundaryTests` asserts it.

A second front end therefore adds a way in and a way to render, and nothing else. `--json` output is
identical whichever one you reach, because there is only one copy of it.

## Development

```bash
dotnet build src/Noogen.Backlog.slnx
dotnet test  src/Noogen.Backlog.slnx
```

Once it passes, `./scripts/deploy.ps1` packs it and makes it the tool and skill on this machine.
See [Distributing](#distributing).

The skill in `.claude/skills/backlog` is both the one agents working *on* this repo load and the
one the build ships to everyone else — edit it there and deploy; there is no second copy.

Conventions: no tuples, no primary constructors, no `is { }` patterns. Warnings are errors.
Test methods are named `MethodUnderTest_Scenario_ExpectedBehavior`.
Commits follow [Conventional Commits](https://www.conventionalcommits.org/).
