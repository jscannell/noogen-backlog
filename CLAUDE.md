# CLAUDE.md

Company-wide work-item tracker. Markdown tickets in a Google Drive shared drive, indexed by a
Google Sheet, prioritised with WSJF, run as Kanban. Read `README.md` first — it covers the model,
setup, and command surface.

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
- **`Noogen.Backlog.Tests`** — xUnit over the fakes.

## The invariants

These are the things to be careful about; most of the design follows from them.

1. **The tab is the state.** `BacklogPhase` carries `TabName`, `IsRanked`, `UsesLiveFormulas`, and
   the transition table. Derive behaviour from the phase — never re-test a status string at a
   call site, and never add a status column to the Backlog tab.

2. **Append before delete.** A transition is a cross-tab row move: two non-atomic API calls.
   `TicketMover` appends to the destination *then* deletes from the source, so an interruption
   duplicates a ticket rather than losing one. `doctor` detects the duplicate. Never reverse this,
   and never "optimise" it into a delete-first.

3. **The Sheet owns `cod`, `wsjf`, `rank`.** On the Backlog tab those are live formulas and the
   store must not write values into them. On In Progress and Archive they are frozen static
   values. `SheetIndex.BuildRow` is the only place that decides which, via `UsesLiveFormulas()`.

4. **Columns resolve by header name, never by position.** `SheetTable` maps name → index off row 1.
   Humans reorder and add columns; that must keep working. Formulas are built from resolved
   column letters for the same reason.

5. **Timestamps are real Sheets datetime serials, not text.** A serial is a fractional day
   interpreted against the *spreadsheet's own timezone*, which buys native local rendering,
   correct sorting across DST, and working date filters. Three consequences to respect:
   - `SheetTime.ToSerial`/`FromSerial` **quantise to whole seconds**. A double cannot hold most
     instants exactly, and without it 23:59:00 reads back as 23:58:59.9999998.
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
   events in prose. Never reintroduce them into frontmatter; a stale duplicate a human has to
   hand-maintain is worse than no duplicate. Legacy documents carrying them are still read.

9. **User text is escaped before it reaches a cell.** `SheetIndex.EscapeUserText` prefixes a
   leading `=`, `+`, `-`, or `@` with an apostrophe. A ticket title is untrusted input.

10. **Archive, never delete.** Nothing here may trash a Drive file. Archiving moves it.

11. **Document parse failures are `FormatException`.** `doctor` catches that to report a bad file
    and continue; anything else aborts the sweep.

12. **`--json` is always UTC.** Human output localises; the machine contract does not. The skill
    and the future agent toolset parse that output, so a representation that moves with a config
    setting would be a trap.

13. **The refresh token is never written in the clear.** `ProtectedDataStore` replaces Google's
    `FileDataStore` and encrypts through the OS keystore — DPAPI, Keychain, or Secret Service.
    Where no keystore exists the fallback stores plaintext and *says so*, and the CLI warns;
    silently writing plaintext while looking encrypted would be the worst outcome. Never
    reintroduce `FileDataStore`, and never store a key beside the ciphertext — that is theatre.
    Be honest in messaging about the limit: this defeats file harvesting and offline reuse, not
    malware already running as the user.

14. **The OAuth client is embedded at build time** from a gitignored `oauth.json` at the repo root
    (`BacklogOAuthClientFile` to override). Resolution is environment → local file → embedded, so
    an override beats the baked-in default. A build without the file must keep working — that is
    what a contributor without the secret gets — so never make the embedding required.

15. **Credential precedence is `NOOGEN_BACKLOG_CREDENTIALS` → signed-in user → ADC**, and ADC is
    never volunteered on a workstation, where it is machine-global and usually belongs to
    something else. `GoogleCredentialResolver.Choose` isolates the decision so it stays testable.
    Credentials resolve once at startup — never lazily from inside a request, because resolution
    can need I/O and, for a new user, a browser.

## Conventions

- No tuples. No primary constructors. No `is { }` pattern.
- Interface-typed collection properties; `[JsonIgnore(WhenWritingDefault)]` on JSON output models.
- Every command supports `--json`; that is the agent contract, so keep the shapes stable.
- Always write tests. Update READMEs and the skill when the command surface changes.
- [Conventional Commits](https://www.conventionalcommits.org/) (`feat(backlog): ...`).

## Testing

`TestBacklog.CreateAsync()` builds an initialised backlog over `FakeSheetsGateway` /
`FakeDriveGateway`. The fakes store raw cell content, so tests can assert formula-vs-frozen-value
directly. `FakeSheetsGateway.FailNextDeleteRow` injects the fault that proves invariant 2.
`TestClock` drives lead/cycle time without real waiting.
