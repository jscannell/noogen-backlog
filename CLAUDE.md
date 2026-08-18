# CLAUDE.md

Company-wide work-item tracker. One Google Doc per ticket in a Drive shared drive, written and read
as markdown, indexed by a Google Sheet, prioritised with WSJF, run as Kanban. Read `README.md`
first — it covers the model, setup, and command surface.

**To use the backlog** (rather than work on it), invoke the `backlog` skill in
`.claude/skills/backlog/`.

## Architecture

Flow: **CLI verb → `IBacklogStore` → `SheetIndex` / `TicketMover` → gateways → Drive + Sheets.**
Solution: `src/Noogen.Backlog.slnx`.

- **`Noogen.Providers.GoogleWorkspace`** — `GoogleCredentialResolver`, `UserCredentialStore`, the
  `Security/` token protection, and the Drive/Sheets gateways. The gateway interfaces are the test
  seam — everything above them runs against in-memory fakes. `A1` is the only place that builds
  range strings. No domain-wide delegation: shared drives take a service account as a direct member.
- **`Noogen.Backlog`** — all logic. Referenced directly by the future `BacklogToolset` in
  `Noogen.Agent`, which is why nothing here may depend on the CLI.
- **`Noogen.Backlog.Cli`** — thin shell. Arg parsing, output formatting, exit codes. No logic.
  Also what the build embeds things *into*: the OAuth client and the Claude Code skill, which is
  why the nupkg is the whole distribution. `scripts/deploy.ps1` packs it, replaces the global tool
  and installs the skill — that is the loop after a change.
- **`Noogen.Backlog.Tests`** — xUnit over the fakes.
- **`Noogen.Providers.GoogleWorkspace.Tests`** — xUnit over a stub HTTP transport. The gateways are
  translators from our vocabulary into Google's REST shapes, so what they put on the wire *is* the
  behaviour; `StubHttpClientFactory` swaps the handler and leaves the rest of Google's pipeline
  intact. Auth, token protection and the OAuth client live here too — everything except the
  build-time embedding, which can only be asserted against the CLI assembly that carries it.

## The invariants

These are the things to be careful about; most of the design follows from them.

1. **The tab is the state.** `BacklogPhase` carries `TabName`, `IsRanked`, `UsesLiveFormulas`, and
   the transition table. Derive behaviour from the phase — never re-test a status string at a
   call site, and never add a status column to the Backlog tab.

2. **Append before delete.** A transition is a cross-tab row move: two non-atomic API calls.
   `TicketMover` appends to the destination *then* deletes from the source, so an interruption
   duplicates a ticket rather than losing one. `doctor` detects the duplicate. Never reverse this,
   and never "optimise" it into a delete-first.

3. **The Sheet owns `Cost of Delay`, `WSJF`, `Rank`.** On the Backlog tab those are live formulas
   and the store must not write values into them. On In Progress and Archive they are frozen static
   values. `SheetIndex.BuildRow` is the only place that decides which, via `UsesLiveFormulas()`.

4. **Columns resolve by header name, never by position, and a name has more than one spelling.**
   `SheetTable` maps name → index off row 1. Humans reorder and add columns; that must keep
   working. Formulas are built from resolved column letters for the same reason.

   The names are the words a person would use — `Business Value`, not `bv` — because the Sheet is
   the face of the backlog. Backlogs created before that still carry the short names, so every
   header goes through `SheetSchema.Canonical`, which accepts the current name, the legacy short
   name, and the same words punctuated differently. **Nothing ever relabels an existing header
   row**: `init` only writes headers into an empty tab, and a header row is a human's to edit —
   they may have styled, filtered, or referenced it from elsewhere.

   The consequence to respect: anything that decides *what to write into a cell* must walk
   `SheetTable.CanonicalHeaders`, not `Headers`. `BuildCell` switches on the schema's names, so
   walking the literal header row of a legacy tab would match nothing and blank the row.
   `Canonical` matches whole normalised strings only — a prefix match would confuse `id` with
   `doc_id`, which are unrelated (see invariant 8).

5. **Timestamps are real Sheets datetime serials, not text.** A serial is a fractional day
   interpreted against the *spreadsheet's own timezone*, which buys native local rendering,
   correct sorting across DST, and working date filters. Four consequences to respect:
   - `SheetTime.ToSerial`/`FromSerial` **quantise to whole seconds**. A double cannot hold most
     instants exactly, and without it 23:59:00 reads back as 23:58:59.9999998.
   - A serial only *looks* like a time once the cell carries a `DATE_TIME` number format, and a
     format lives on cells, not on columns. `values.append` with `INSERT_ROWS` **inserts** a new
     row rather than filling the formatted blank one below the data, and an inserted row arrives
     unformatted — so `SheetIndex.AppendAsync` reformats the row it just landed in. Without that,
     every ticket shows a five-digit number. `init` applies the same format unbounded below the
     header, which is the repair for rows already written without it.
   - The spreadsheet's `timeZone` property must equal `Config!timezone`. If they diverge every
     stored instant shifts, so `doctor` reports a mismatch as an error and `init` reconciles it.
   - The repeated hour at DST fall-back is genuinely ambiguous on read-back;
     `SheetTime.FromWallClock` resolves it to the earlier instant, bounded at one hour, once a
     year, on metrics reported in days. Spring-forward gaps shift forward rather than throw.

6. **Reads are UNFORMATTED_VALUE, and numbers never route through `ToString()`.** Use
   `SheetTable.Raw` plus `SheetTime.AsNumber` — a formatted read returns locale-rendered text,
   and `double.ToString()` on a European machine emits a comma decimal separator.

7. **Instants are canonical; the timezone is display and interpretation only.** Changing the
   configured zone must never reinterpret history.

8. **The document holds only what a human would edit** — id, title, type, area, owner, scores.
   Timestamps, phase, and work state are machine bookkeeping: the Sheet owns them, Drive's
   `createdTime`/`modifiedTime` back up the first two, and the Activity Log records lifecycle
   events in prose. Never reintroduce them into the document; a stale duplicate a human has to
   hand-maintain is worse than no duplicate.

   Area and owner *do* belong there even though the Sheet carries them too. They are content, not
   bookkeeping, and `reindex` rebuilds a damaged row's content from the document — dropping them
   would leave the Sheet as their only copy, which is the one thing the repair path exists to
   survive.

   Metadata keys are the same names as the columns, canonicalised on the way in, so a person may
   write `job size` or `bv` by hand. `ID` is the ticket id; `Drive File ID` is Drive's file id for
   the document and is *not* in the document — Drive has no paths, so it is the only handle the
   store has for reading and moving that file, and it lives in the Sheet where the store can find
   it without a search.

9. **Everything in the document renders, and the heading is the only home of the id and title.**
   The shape is `# <ID> — <Title>`, then `- **Key:** value` bullets, then prose. No frontmatter:
   Docs is where a non-CLI reader opens a ticket and a `---` block does not survive the conversion,
   so it showed as literal text or vanished into a rule.

   One copy of the title, because two drift. The old format had it in frontmatter *and* in a
   heading that nothing rewrote, so `edit --title` left the heading permanently stale and no check
   could catch it — `doctor` compares the Sheet against the metadata, and those agreed.

   `Serialize` regenerates the heading and the bullet block and copies the rest through verbatim.
   The metadata block ends at the first line that is neither blank nor a bullet, so prose typed
   straight under the title is body and stays put. `SingleLine` collapses newlines in a heading
   value: a title is untrusted input, and a newline there would split the document rather than
   fail.

   Below the bullets is the author's, and eating a human's edit is the one unrecoverable failure
   here — so exactly **two** operations touch the body, both bounded by a heading a person can
   see, and there must never be a third that is not:

   - `AppendActivity` adds one line under `## Activity Log`.
   - `ReplaceSection` replaces the text under one heading, used for exactly two of them:
     `## Description` and `## Acceptance Criteria`, the sections the CLI is expected to author.
     The section ends at the next heading of the same level or higher, so every other section —
     `## Notes`, and any a human added — comes through byte-identical, and a `###` subheading
     inside it is part of it rather than the end of it. A missing heading *inserts* one at the top
     instead of failing or guessing which section was meant: insertion cannot destroy anything,
     which is the property that earns this its exception.

     Acceptance criteria are on that list because leaving them off did not make them a human's:
     it made them nobody's. With no CLI path, every ticket an agent filed kept `- [ ] *TODO*` and
     read downstream as ready when nothing had said what "done" meant. A section a machine is
     expected to write needs a way to write it — but the list is meant to stay at two, and a third
     means deciding again that a machine should be allowed to overwrite that heading.

     Both still seed `*TODO*` when nothing is given, because filing fast is worth keeping. What
     must not come back is a *silent* placeholder: `new` names the sections it left unwritten, on
     stderr, because stdout under `--json` is one document.

     `edit --note` is not a third: it is the same `AppendActivity`, opt-in, carrying the caller's
     own words the way `archive --note` always has. An edit still writes no log entry on its own —
     Docs' revision history is what recovers an overwrite, and "description edited" would add
     nothing to it. What `--note` removes is the second round trip against a document that was
     just written.

     **A section body's own headings must be deeper than the heading that bounds it.** The
     section ends at the next heading of the same level or higher, so a `##` in a description is
     a sibling of `## Description`, not part of it. Accepting one was silent damage: the first
     write looked right, but the section boundary then fell at the body's first heading, so the
     region the *next* write could reach was empty — it inserted instead of replacing and stranded
     the previous body below, out of reach of every write after that, one copy per edit.
     `RequireSectionBody` refuses it from `ReplaceSection` and `BuildInitialBody` alike, before
     anything is written, and names the `###` spelling to use instead.

     The refusal is the whole fix, and the alternative is the trap. Bounding a section by the
     *known* headings instead would let a body hold any heading — and would pull a `## Design` a
     person added between Description and Acceptance Criteria inside the region, where the next
     write deletes it. That is this invariant's promise inverted, so the ambiguity is refused
     rather than resolved: the CLI cannot tell the two apart, because on the page they are the
     same thing.

     `doctor` reads for what the fault left behind — a `##` heading a document holds more than
     once (`RepeatedSections`, kind `duplicate-section`). Drift compares the Sheet against the
     metadata and cannot see the body at all, which is why this class of damage was invisible
     twice over. The stale copies are only removable in Docs.

   Reading is the counterpart, and it is bounded by the same rule. `SectionOf` walks the section
   boundaries through the *same* `FindSection` as `ReplaceSection`, so a reader and a writer can
   never disagree about which lines belong to a human — that is why the walk is one private method
   and not two loops. `show --section` exists so that read-before-write costs one section rather
   than a whole document.

   **`TrimActivityLog` is display only, and must never reach a write.** `show` calls it to drop
   all but the last few log entries; the log is the only prose record of a ticket's life, so a
   trimmed body sent to `UpdateDocAsync` would delete history nothing else holds — the
   unrecoverable failure this invariant exists to prevent. It is called from the `show` command and
   nowhere else, and adding a second caller means proving that caller never writes.

   Everything else about the body stays hands-off. In particular this is not licence to
   "normalise" prose — see invariant 18. Blanking either section is refused rather than treated as
   a clear: every editable *field* is a scalar the Sheet also holds, so emptying one loses
   nothing, while emptying a section throws away writing. Docs' own revision history is what
   recovers an overwrite, which is also why neither edit writes an Activity Log entry.

10. **User text is escaped before it reaches a cell.** `SheetIndex.EscapeUserText` prefixes a
    leading `=`, `+`, `-`, or `@` with an apostrophe. A ticket title is untrusted input.

11. **Archive, never delete.** Nothing here may trash a Drive file. Archiving moves it.

12. **Document parse failures are `FormatException`.** `doctor` catches that to report a bad file
    and continue; anything else aborts the sweep.

13. **`--json` is always UTC.** Human output localises; the machine contract does not. The skill
    and the future agent toolset parse that output, so a representation that moves with a config
    setting would be a trap.

14. **The refresh token is never written in the clear.** `ProtectedDataStore` replaces Google's
    `FileDataStore` and encrypts through the OS keystore — DPAPI, Keychain, or Secret Service.
    Where no keystore exists the fallback stores plaintext and *says so*, and the CLI warns;
    silently writing plaintext while looking encrypted would be the worst outcome. Never
    reintroduce `FileDataStore`, and never store a key beside the ciphertext — that is theatre.
    Be honest in messaging about the limit: this defeats file harvesting and offline reuse, not
    malware already running as the user.

15. **The OAuth client is embedded at build time** from a gitignored `oauth.json` at the repo root
    (`BacklogOAuthClientFile` to override). Resolution is environment → local file → embedded, so
    an override beats the baked-in default. A build without the file must keep working — that is
    what a contributor without the secret gets — so never make the embedding required.

16. **The skill ships inside the binary, and `.claude/skills/backlog` is the only copy.** The
    build embeds that directory as `skill/<relative path>` resources; `backlog install-skill`
    writes them into `~/.claude/skills`. The skill teaches an agent this CLI's verbs, so a skill
    describing verbs the installed tool does not have is worse than no skill — one artifact means
    they cannot be half-updated. Never add a second copy to be "packaged": the directory the build
    reads is the same one this repo's own agents load, which is what keeps them from drifting.

    Two consequences. MSBuild's `%(RecursiveDir)` emits the *build machine's* separator, so a
    resource is really named `skill/references\wsjf.md` on Windows — `EmbeddedSkill` splits on
    both and normalises to forward slashes. And the install target is named from the skill's own
    frontmatter `name:`, not a constant, because Claude Code discovers a skill by its directory
    and a rename that updated only one would install a stale second copy beside the first.

    Installing is refuse-by-default: below `~/.claude` is a person's own configuration, so an
    existing copy that differs is reported file by file and left alone until `--force`. `--force`
    then makes the directory *match*, removing files this version no longer carries, so a dropped
    reference cannot linger and keep teaching something untrue.

17. **Credential precedence is `NOOGEN_BACKLOG_CREDENTIALS` → signed-in user → ADC**, and ADC is
    never volunteered on a workstation, where it is machine-global and usually belongs to
    something else. `GoogleCredentialResolver.Choose` isolates the decision so it stays testable.
    Credentials resolve once at startup — never lazily from inside a request, because resolution
    can need I/O and, for a new user, a browser.

18. **Markdown is the format; a Google Doc is the file.** `CreateDocAsync` uploads `text/markdown`
    against a `application/vnd.google-apps.document` metadata type so Drive converts on the way in,
    and `ReadDocAsync` *exports* rather than downloads — a Doc has no stored bytes, and `alt=media`
    on one fails. `UpdateDocAsync` restates the Doc type for the same reason: without it an upload
    of markdown turns the Doc back into a markdown file. Send the same type in both places anywhere
    and you have silently undone this.

    The reason is the Sheet's `Title` link. A `text/markdown` file's `webViewLink` is a Drive
    preview of raw text; a Doc's is a docs.google.com URL that renders. Nothing in `SheetIndex`
    knows about any of this — the link was always `webViewLink`, and changing what the file *is* is
    what changed where it opens.

    The cost, and the thing to respect: the round trip is not byte-exact. Docs stores paragraphs
    and lists, so an export comes back in Docs' style — hard-wrapped lines rejoined into one
    paragraph, `_italic_` renormalised to `*italic*`, backslash-escaped punctuation (`\-`, `\--`),
    two trailing spaces on every list item but the last. Anything we *generate* should already be
    in that style so a new document survives its first save unchanged, which is why
    `BuildInitialBody` writes `*TODO*` and not `_TODO_`. All of it is stable rather than
    compounding: verified live across three consecutive writes, where the body was byte-identical
    apart from the appended log entry. The metadata parser must keep tolerating those shapes
    (`TicketDocumentTests` pins them), and the body must keep being copied through exactly as it
    came back. Never "normalise" the prose to undo Docs' escaping: that is rewriting a human's
    text, which invariant 9 exists to prevent.

    Two things are *not* cosmetic, and both are the same shape: Docs rewrote a value we have to
    compare against the Sheet, so the reader undoes it and the writer keeps emitting plain text.

    **Autolinking.** Docs turns anything email- or URL-shaped into a link on import, so
    `- **Owner:** j@noogen.ai` exports as `- **Owner:** [j@noogen.ai](mailto:j@noogen.ai)`.
    `TicketDocument.Unlink` unwraps a value that is wholly a link, and must keep doing so:
    `reindex` takes area and owner from the document, so without it that link is what lands in the
    Sheet — and `DescribeDrift` compares neither, so `doctor` reports healthy while the column is
    wrong. Whole values only; a link inside prose is the author's.

    **Escaping.** The same export backslashes punctuation, so a title anyone would write —
    `Fix the sign-in flow`, `Rename follow_up` — comes back as `Fix the sign\-in flow`. That one
    *is* compared: `DescribeDrift` checks the title, so every hyphenated ticket drifted the moment
    it was created. `TicketDocument.Unescape` strips it from the heading and the field values.

    Both loops settle rather than compound, and neither has a writer half: `\-` and `-` import to
    the same character, so `Serialize` writes what a human would type and Docs re-decorates it on
    the next import. **Scalars only.** Never unescape or unlink the body — below the bullets is the
    author's, the escapes render as they meant them, and rewriting prose to taste is exactly what
    invariant 9 exists to prevent.

19. **A rate-limit response is a rejection, so it is retried; nothing else is.**
    `RateLimitRetryHandler` sits on both services and retries 429 — and Drive's older 403 spelling
    of the same thing — with equal-jitter backoff, honouring `Retry-After` where Google sends one.

    Retrying is safe *because* of what a 429 is: Google refused the request, it was never
    half-applied, so a retried append cannot duplicate a row. That is exactly what invariant 2
    exists to survive when it is *not* true, and it is why nothing here retries a timeout.

    Three lines to hold. The reason decides on a 403: `rateLimitExceeded` and
    `userRateLimitExceeded` refill on their own, `dailyLimitExceeded` and `quotaExceeded` do not,
    and waiting eight seconds for a daily quota only delays the same failure. 503 and transport
    faults are already the library's `DefaultExponentialBackOffPolicy` — handling them here would
    double the waiting. And `ConfigurableMessageHandler.NumTries` is the real retry budget,
    defaulting to 3, so `Attach` raises it; a handler that returns `true` past it buys a wait and
    then the same error.

    Above the gateways nothing knows any of this, which is the point — but a silent 8-second pause
    reads as a hang, so `IRetryListener` lets the CLI say why it is quiet, on stderr, because
    stdout under `--json` is one document. When the retries run out the CLI reports
    `rate-limited` and exits 4.

20. **Search reads two sources, and the join is on `Drive File ID`.** The Sheet holds the names and
    no prose; Drive's index holds the prose and lags. `find` matches names as substrings across all
    three tabs, asks Drive for the rest, and unions them — reporting on every hit which half
    matched, because the two are trusted differently.

    Neither half is optional. Drive is the *only* route to a description or acceptance criteria, and
    it is eventually consistent — the ticket filed a minute ago, which is precisely the one a
    duplicate check is looking for, may not be indexed yet. It also matches whole terms, so `limit`
    misses *limiting*. The Sheet half covers both gaps, and covers nothing Drive covers well.

    Two things to hold. The union joins on the document id, never on the file's name: the name
    happens to start with the ticket id, but the Sheet is what *owns* the mapping (invariant 8), and
    joining there is also what drops a Drive hit on something that is not a ticket. And the Drive
    query is scoped by `corpora=drive`, **never by parent folder** — `in parents` matches immediate
    children only, and an archived document lives two levels down under year and quarter, so a
    parent-scoped query silently answers for active tickets alone. It cannot be left unscoped
    either: the credential holds full Drive (invariant 17's scope note), so an unqualified
    `fullText` query sweeps everything the signed-in person can read.

## Conventions

- No tuples. No primary constructors. No `is { }` pattern.
- Interface-typed collection properties; `[JsonIgnore(WhenWritingDefault)]` on JSON output models.
- Every command supports `--json`; that is the agent contract, so keep the shapes stable. It is
  emitted **compact** — an agent pays for indentation and gets nothing back, and whitespace was
  never part of what the shapes promise. Narrowing is the caller's to ask for: `--fields` on the
  list-shaped verbs, `--section` on `show`. Both are projections, so they must not invent a key —
  a field a ticket does not carry stays absent rather than becoming null.
- Every verb declares the options it reads in `Verbs`, and an undeclared option is a usage error.
  The parser collects any `--name`, so a flag nothing reads is invisible: the command runs, does
  nothing, and reports success. Adding a flag to a command means adding it to that table too.
  The same table caps positionals — a ticket id or nothing — because an argument past that is
  almost always a value PowerShell split at an unescaped double quote, and dropping it silently
  truncated the ticket. Every prose option therefore has two paths that never reach the command
  line at all (`--<name>-file`, and `--<name> -` for stdin); `TextInput.ReadProse` reads all three
  spellings for any option name, decoding UTF-8 after a BOM and falling back to the console
  encoding rather than emitting `U+FFFD`. Only one option per command may read stdin — there is
  one pipe, and the second reader would report the text as empty, naming the wrong problem.
- Always write tests, named `MethodUnderTest_Scenario_ExpectedBehavior`. Update READMEs and the
  skill when the command surface changes.
- [Conventional Commits](https://www.conventionalcommits.org/) (`feat(backlog): ...`).

## Testing

`TestBacklog.CreateAsync()` builds an initialised backlog over `FakeSheetsGateway` /
`FakeDriveGateway`. The fakes store raw cell content, so tests can assert formula-vs-frozen-value
directly. `FakeSheetsGateway.FailNextDeleteRow` injects the fault that proves invariant 2.
`TestClock` drives lead/cycle time without real waiting. `TestBacklog.UseLegacyHeadersAsync()`
rewrites the header rows back to the old short names — the seam for invariant 4's promise that an
existing spreadsheet keeps working and keeps its headers.

Below the gateway interfaces there are no fakes to hide behind, so `StubHttpHandler` records the
requests Google would have received and replays canned responses in order. That is the seam for
anything the gateways decide — render options, Drive query escaping, shared-drive flags, the batch
requests they build — and it covers the resumable-upload and export paths too. The markdown-to-Doc
conversion is only visible on the wire (two different mime types in one request), so that is where
it is asserted; above the gateway the fakes store the markdown verbatim, and the shapes Docs sends
back are pinned against the parser in `TicketDocumentTests` instead.

`ReversingTokenProtector` stands in for the OS keystore so token tests assert what reached disk
without depending on whichever keystore the machine has; the real one is still exercised for a
round trip.
