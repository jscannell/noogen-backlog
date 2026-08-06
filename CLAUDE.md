# CLAUDE.md

Company-wide work-item tracker. Markdown tickets in a Google Drive shared drive, indexed by a
Google Sheet, prioritised with WSJF, run as Kanban. Read `README.md` first — it covers the model,
setup, and command surface.

**To use the backlog** (rather than work on it), invoke the `backlog` skill in
`.claude/skills/backlog/`.

## Architecture

Flow: **CLI verb → `IBacklogStore` → `SheetIndex` / `TicketMover` → gateways → Drive + Sheets.**
Solution: `src/Noogen.Backlog.slnx`.

- **`Noogen.Providers.GoogleWorkspace`** — `DriveClientFactory` / `SheetsClientFactory` resolving
  credentials through the **ADC chain**, plus `IDriveGateway` / `ISheetsGateway` and their
  Google-backed implementations. The gateway interfaces are the test seam — everything above them
  runs against in-memory fakes. `A1` is the only place that builds range strings.
  No domain-wide delegation: shared drives take a service account as a direct member.
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

5. **Timestamps are ISO-8601 text.** The affected columns are set to the TEXT number format at
   init, because Sheets otherwise coerces them into locale-formatted date serials and the
   round-trip read returns something we never wrote.

6. **User text is escaped before it reaches a cell.** `SheetIndex.EscapeUserText` prefixes a
   leading `=`, `+`, `-`, or `@` with an apostrophe. A ticket title is untrusted input.

7. **Archive, never delete.** Nothing in this codebase may trash a Drive file. Archiving moves it.

8. **Document parse failures are `FormatException`.** `doctor` catches that to report a bad file
   and continue; anything else aborts the sweep.

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
