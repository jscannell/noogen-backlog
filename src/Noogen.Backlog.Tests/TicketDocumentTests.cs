namespace Noogen.Backlog.Tests
{
    public class TicketDocumentTests
    {
        static Ticket Sample() => new()
        {
            Id = "NG-0007",
            Title = "WSJF index tool",
            Type = TicketType.Feature,
            Area = "agent",
            Owner = "jason",
            Phase = BacklogPhase.InProgress,
            State = WorkState.Blocked,
            BlockedReason = "waiting on Drive API quota",
            BlockedAt = new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero),
            StartedAt = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero),
            Created = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero),
            Updated = new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero),
            Score = new WsjfScore
            {
                BusinessValue = 8,
                TimeCriticality = 3,
                RiskReductionOpportunityEnablement = 2,
                JobSize = 5
            }
        };

        [Fact]
        public void Parse_DocumentWrittenBySerialize_RoundTripsEveryHumanEditableField()
        {
            var original = Sample();
            var serialized = TicketDocument.Serialize(original, "## Description\n\nSomething.");

            var parsed = TicketDocument.Parse(serialized).Ticket;

            Assert.Equal(original.Id, parsed.Id);
            Assert.Equal(original.Title, parsed.Title);
            Assert.Equal(original.Type, parsed.Type);
            Assert.Equal(original.Area, parsed.Area);
            Assert.Equal(original.Owner, parsed.Owner);
            Assert.Equal(original.Score.BusinessValue, parsed.Score.BusinessValue);
            Assert.Equal(original.Score.TimeCriticality, parsed.Score.TimeCriticality);
            Assert.Equal(original.Score.RiskReductionOpportunityEnablement, parsed.Score.RiskReductionOpportunityEnablement);
            Assert.Equal(original.Score.JobSize, parsed.Score.JobSize);
        }

        [Fact]
        public void Serialize_Always_OpensWithAHeadingThatRendersAsMarkdown()
        {
            // Drive is where a person who does not use the CLI reads a ticket, and it does not
            // special-case a '---' frontmatter block the way a code host does.
            var serialized = TicketDocument.Serialize(Sample(), "body");

            Assert.StartsWith("# NG-0007 — WSJF index tool\n", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("---", serialized);
        }

        [Fact]
        public void Serialize_Always_HoldsTheIdAndTitleOnlyInTheHeading()
        {
            // The duplication this replaces was not merely untidy: nothing rewrote the heading, so
            // an edited title left it stale forever and doctor could not see it — the Sheet and the
            // frontmatter agreed with each other.
            var serialized = TicketDocument.Serialize(Sample(), "body");

            Assert.Equal(1, CountOccurrences(serialized, "WSJF index tool"));
            Assert.Equal(1, CountOccurrences(serialized, "NG-0007"));
            Assert.DoesNotContain($"**{SheetSchema.Title}:**", serialized);
            Assert.DoesNotContain($"**{SheetSchema.Id}:**", serialized);
        }

        [Fact]
        public void Serialize_TitleHasChangedSinceTheDocumentWasWritten_RewritesTheHeading()
        {
            var document = TicketDocument.Parse(TicketDocument.Serialize(Sample(), "## Description\n\nSomething."));
            document.Ticket.Title = "WSJF index tool, renamed";

            var rewritten = document.Serialize();

            Assert.StartsWith("# NG-0007 — WSJF index tool, renamed\n", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("# NG-0007 — WSJF index tool\n", rewritten);
            Assert.Equal("WSJF index tool, renamed", TicketDocument.Parse(rewritten).Ticket.Title);
        }

        [Fact]
        public void Serialize_Always_OmitsMachineBookkeepingAHumanWouldHaveToMaintain()
        {
            // Timestamps, phase, and work state live in the Sheet and in Drive's file metadata.
            // Duplicating them here would mean a person hand-editing ISO-8601, and a stale copy
            // is worse than no copy. The Activity Log carries the same story in prose.
            var serialized = TicketDocument.Serialize(Sample(), "body");

            Assert.DoesNotContain($"**{SheetSchema.Created}:**", serialized);
            Assert.DoesNotContain($"**{SheetSchema.Updated}:**", serialized);
            Assert.DoesNotContain($"**{SheetSchema.StartedAt}:**", serialized);
            Assert.DoesNotContain($"**{SheetSchema.BlockedAt}:**", serialized);
            Assert.DoesNotContain($"**{SheetSchema.ArchivedAt}:**", serialized);
            Assert.DoesNotContain("**phase:**", serialized);
            Assert.DoesNotContain($"**{SheetSchema.State}:**", serialized);
        }

        [Fact]
        public void Serialize_Always_KeepsAreaAndOwnerInTheDocument()
        {
            // They are content, not bookkeeping. reindex rebuilds a damaged row's content from the
            // document, so the Sheet must not be their only copy.
            var serialized = TicketDocument.Serialize(Sample(), "body");

            Assert.Contains("- **Area:** agent", serialized);
            Assert.Contains("- **Owner:** jason", serialized);
        }

        [Fact]
        public void Serialize_Always_NamesTheFieldsTheWayAPersonWouldSayThem()
        {
            var serialized = TicketDocument.Serialize(Sample(), "body");

            Assert.Contains("- **Business Value:** 8", serialized);
            Assert.Contains("- **Time Criticality:** 3", serialized);
            Assert.Contains("- **Risk & Opportunity:** 2", serialized);
            Assert.Contains("- **Job Size:** 5", serialized);
        }

        [Fact]
        public void Parse_HeadingUsesAPlainDash_StillSplitsTheIdFromTheTitle()
        {
            // We write an em dash; a person retyping the heading in Drive will not.
            var ticket = TicketDocument.Parse("# NG-0001 - Something\n\n- **Type:** bug\n\nbody").Ticket;

            Assert.Equal("NG-0001", ticket.Id);
            Assert.Equal("Something", ticket.Title);
        }

        [Fact]
        public void Parse_TitleContainsADashOrAColon_SplitsAtTheSeparatorAfterTheId()
        {
            var dashed = TicketDocument.Parse("# NG-0001 — Fix the A - B handoff\n\nbody").Ticket;
            Assert.Equal("NG-0001", dashed.Id);
            Assert.Equal("Fix the A - B handoff", dashed.Title);

            var colonned = TicketDocument.Parse("# NG-0001 — Noogen Backlog: a tracker\n\nbody").Ticket;
            Assert.Equal("NG-0001", colonned.Id);
            Assert.Equal("Noogen Backlog: a tracker", colonned.Title);
        }

        [Fact]
        public void Parse_FieldPutsTheColonOutsideTheBold_ReadsItAnyway()
        {
            var ticket = TicketDocument.Parse("# NG-0001 — Something\n\n* **Owner**: jason\n\nbody").Ticket;

            Assert.Equal("jason", ticket.Owner);
        }

        [Fact]
        public void Parse_FieldUsesADifferentSpelling_ResolvesItToTheColumn()
        {
            var document = """
                # NG-0001 — Something

                - **bv:** 8
                - **job size:** 5
                - **risk_opportunity:** 2

                body
                """;

            var ticket = TicketDocument.Parse(document).Ticket;

            Assert.Equal(8, ticket.Score.BusinessValue);
            Assert.Equal(5, ticket.Score.JobSize);
            Assert.Equal(2, ticket.Score.RiskReductionOpportunityEnablement);

            // A known column under another spelling is the column, not a field a human invented, so
            // it must not survive as an extra and leave the document carrying both.
            Assert.False(ticket.ExtraFields.ContainsKey("bv"));
            Assert.Contains("- **Business Value:** 8", TicketDocument.Serialize(ticket, "body"));
        }

        [Fact]
        public void Parse_ProseSitsBetweenTheMetadataAndTheFirstHeading_KeepsItInTheBody()
        {
            // The store regenerates the heading and the bullets on every write. Anything below them
            // is the author's, including a sentence they typed before the first '##'.
            var document = """
                # NG-0001 — Something

                - **Type:** bug

                A sentence someone typed straight under the title.

                ## Description

                More.
                """;

            var parsed = TicketDocument.Parse(document);

            Assert.StartsWith("A sentence someone typed straight under the title.", parsed.Body, StringComparison.Ordinal);
            Assert.Contains("A sentence someone typed straight under the title.", parsed.Serialize());
        }

        [Fact]
        public void Parse_DocumentAlsoCarriesAnIdBullet_LetsTheHeadingWin()
        {
            var ticket = TicketDocument.Parse("# NG-0001 — Real title\n\n- **ID:** NG-9999\n- **Title:** Stale title\n\nbody").Ticket;

            Assert.Equal("NG-0001", ticket.Id);
            Assert.Equal("Real title", ticket.Title);
        }

        /// <summary>
        /// A deliberately pessimistic stand-in for what Drive gives back. A ticket is stored as a
        /// Google Doc so that opening it renders, so every save round-trips through Docs' own
        /// model: markdown in, paragraphs and lists stored, markdown back out in Docs' house style
        /// rather than ours.
        ///
        /// One rewrite here was observed against real Drive — the two trailing spaces Docs puts on
        /// every list item but the last. The padded bullet marker and the blank line between items
        /// were not: the export measured used neither. They stay because both are ordinary
        /// markdown that a differently-configured Docs, or a person editing in Drive, may produce,
        /// and the cost of tolerating them is a wider regex we already have.
        ///
        /// The metadata block has to survive all of it. If it does not, the next edit parses no
        /// fields and writes the row back blank.
        /// </summary>
        static string AsDocsWouldExport(string markdown) =>
            markdown.Replace("\n- ", "  \n\n-   ", StringComparison.Ordinal);

        [Fact]
        public void Parse_DocumentCameBackFromDocsInItsOwnStyle_StillReadsEveryField()
        {
            var original = Sample();
            var exported = AsDocsWouldExport(TicketDocument.Serialize(original, "## Description\n\nSomething."));

            var parsed = TicketDocument.Parse(exported).Ticket;

            Assert.Equal(original.Id, parsed.Id);
            Assert.Equal(original.Title, parsed.Title);
            Assert.Equal(original.Type, parsed.Type);
            Assert.Equal(original.Area, parsed.Area);
            Assert.Equal(original.Owner, parsed.Owner);
            Assert.Equal(original.Score.BusinessValue, parsed.Score.BusinessValue);
            Assert.Equal(original.Score.TimeCriticality, parsed.Score.TimeCriticality);
            Assert.Equal(original.Score.RiskReductionOpportunityEnablement, parsed.Score.RiskReductionOpportunityEnablement);
            Assert.Equal(original.Score.JobSize, parsed.Score.JobSize);

            // No field survived as an extra: a padded bullet Docs wrote is the same field we wrote,
            // and treating it as one a human invented would duplicate the whole block on save.
            Assert.Empty(parsed.ExtraFields);
        }

        [Fact]
        public void Serialize_DocumentCameBackFromDocsInItsOwnStyle_RestoresOurFormatting()
        {
            // The store rewrites the heading and the bullets on every save, so Docs' style is
            // absorbed rather than compounded — the document we send is always ours.
            var ours = TicketDocument.Serialize(Sample(), "## Description\n\nSomething.");

            var returned = TicketDocument.Parse(AsDocsWouldExport(ours)).Serialize();

            Assert.Contains("- **Business Value:** 8", returned, StringComparison.Ordinal);
            Assert.DoesNotContain("-   **", returned, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DocsAutolinkedAnEmailFieldValue_ReadsTheAddressNotTheLink()
        {
            // Observed against real Drive: Docs autolinks anything email-shaped on import, so an
            // owner written as plain text exports as a markdown link. reindex takes owner from the
            // document, so left alone this writes '[j@noogen.ai](mailto:j@noogen.ai)' into the
            // Sheet — and doctor compares neither owner nor area, so nothing would report it.
            var document = "# NG-0001 — Something\n\n- **Owner:** [j@noogen.ai](mailto:j@noogen.ai)\n\nbody";

            Assert.Equal("j@noogen.ai", TicketDocument.Parse(document).Ticket.Owner);
        }

        [Fact]
        public void Parse_FieldValueIsABareAutolink_ReadsTheTextInside()
        {
            var document = "# NG-0001 — Something\n\n- **Owner:** <j@noogen.ai>\n\nbody";

            Assert.Equal("j@noogen.ai", TicketDocument.Parse(document).Ticket.Owner);
        }

        [Fact]
        public void Serialize_OwnerCameBackAutolinked_WritesThePlainAddressAgain()
        {
            // The loop has to settle: we unwrap on read and write plain text, Docs re-links it on
            // the next import, and the read after that unwraps the same way. Nothing accumulates.
            var document = "# NG-0001 — Something\n\n- **Owner:** [j@noogen.ai](mailto:j@noogen.ai)\n\nbody";

            var round = TicketDocument.Parse(document).Serialize();

            Assert.Contains("- **Owner:** j@noogen.ai\n", round, StringComparison.Ordinal);
            Assert.DoesNotContain("mailto:", round, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_ProseContainsALink_LeavesItAlone()
        {
            // Only whole field values are unwrapped. A link a person wrote in the body is theirs.
            var body = "## Notes\n\nSee [the docs](https://example.invalid/x) for context.";
            var parsed = TicketDocument.Parse($"# NG-0001 — Something\n\n- **Type:** bug\n\n{body}");

            Assert.Equal(body, parsed.Body);
        }

        [Fact]
        public void Parse_DocsPaddedTheBulletMarker_StillReadsTheField()
        {
            var ticket = TicketDocument.Parse("# NG-0001 — Something\n\n-   **Owner:** jason\n\nbody").Ticket;

            Assert.Equal("jason", ticket.Owner);
        }

        [Fact]
        public void Parse_DocsSeparatedTheBulletsWithBlankLines_ReadsTheWholeMetadataBlock()
        {
            // The block ends at the first line that is neither blank nor a bullet. A blank line
            // between two bullets must not be read as that boundary, or every field below the
            // first one is lost and the next save writes them back empty.
            var document = """
                # NG-0001 — Something

                -   **Type:** bug

                -   **Area:** agent

                -   **Job Size:** 5

                ## Description
                """;

            var ticket = TicketDocument.Parse(document).Ticket;

            Assert.Equal(TicketType.Bug, ticket.Type);
            Assert.Equal("agent", ticket.Area);
            Assert.Equal(5, ticket.Score.JobSize);
        }

        [Fact]
        public void Parse_DocsEscapedPunctuationInTheProse_LeavesTheBodyExactlyAsItCameBack()
        {
            // Below the bullets is the author's. We do not unescape what Docs escaped: rewriting
            // prose to taste is how an edit gets eaten, and the escapes render as the author meant.
            var body = "## Acceptance Criteria\n\n- [ ] \\-\\- not a rule, escaped by Docs";
            var document = $"# NG-0001 — Something\n\n-   **Type:** bug\n\n{body}";

            Assert.Equal(body, TicketDocument.Parse(document).Body);
        }

        [Theory]
        [InlineData("Fix the sign\\-in flow", "Fix the sign-in flow")]
        [InlineData("Rename follow\\_up to next\\_step", "Rename follow_up to next_step")]
        [InlineData("Escape \\* and \\# and \\[brackets\\]", "Escape * and # and [brackets]")]
        public void Parse_DocsEscapedPunctuationInTheTitle_ReadsThePlainTitle(string heading, string expected)
        {
            // Docs escapes anything markup-shaped on export, so a hyphen or an underscore in a
            // title comes back backslashed. Left alone the document disagrees with the Sheet from
            // the first read and doctor reports drift on a ticket nobody has touched.
            var ticket = TicketDocument.Parse($"# NG-0001 — {heading}\n\n- **Type:** bug\n\nbody").Ticket;

            Assert.Equal(expected, ticket.Title);
        }

        [Fact]
        public void Parse_DocsEscapedPunctuationInAFieldValue_ReadsThePlainValue()
        {
            // reindex rebuilds a damaged row's area and owner from the document, so a backslash
            // surviving here is a backslash written into the Sheet.
            var document = "# NG-0001 — Something\n\n-   **Area:** platform\\_rebrand\n-   **Owner:** ana\\-maria\n\nbody";

            var ticket = TicketDocument.Parse(document).Ticket;

            Assert.Equal("platform_rebrand", ticket.Area);
            Assert.Equal("ana-maria", ticket.Owner);
        }

        [Fact]
        public void Parse_DocsEscapedTheUnderscoreInAFieldKey_StillCanonicalisesIt()
        {
            var ticket = TicketDocument.Parse("# NG-0001 — Something\n\n- **job\\_size:** 5\n\nbody").Ticket;

            Assert.Equal(5, ticket.Score.JobSize);
        }

        [Fact]
        public void Parse_ValueHasALiteralBackslash_KeepsIt()
        {
            // A backslash before something that is not punctuation is a character somebody typed.
            var ticket = TicketDocument.Parse("# NG-0001 — Something\n\n- **Area:** c:\\\\temp\\path\n\nbody").Ticket;

            Assert.Equal("c:\\temp\\path", ticket.Area);
        }

        [Fact]
        public void Serialize_TitleCameBackEscaped_WritesThePlainTitleAgain()
        {
            // The loop has to settle: we strip on read and write plain text, Docs re-escapes it on
            // the next import, and the read after that strips the same way. Nothing accumulates.
            var round = TicketDocument.Parse("# NG-0001 — Fix the sign\\-in flow\n\n- **Type:** bug\n\nbody").Serialize();

            Assert.StartsWith("# NG-0001 — Fix the sign-in flow\n", round, StringComparison.Ordinal);
            Assert.Equal(round, TicketDocument.Parse(round).Serialize());
        }

        [Fact]
        public void Serialize_AppliedTwice_ProducesTheSameDocument()
        {
            var once = TicketDocument.Serialize(Sample(), "body");
            var twice = TicketDocument.Parse(once).Serialize();

            Assert.Equal(once, twice);
        }

        [Fact]
        public void Serialize_BodyHasHandWrittenProseInEverySection_CopiesItThroughVerbatim()
        {
            var body = TicketDocument.AppendActivity(
                TicketDocument.BuildInitialBody(Sample(), "A described thing."),
                new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero),
                "started")
                .Replace("- [ ] *TODO*", "- [x] The gateway round-trips\n- [ ] doctor reports it")
                .Replace("## Notes\n", "## Notes\n\nSpoke to the platform team; they want the batch API.\n");

            var round = TicketDocument.Parse(TicketDocument.Serialize(Sample(), body));

            Assert.Equal(body.TrimEnd('\n'), round.Body);
        }

        [Fact]
        public void Serialize_TicketCarriesFieldsAHumanAdded_PreservesThem()
        {
            var document = """
                # NG-0001 — Something

                - **epic:** platform-rebrand

                body
                """;

            var ticket = TicketDocument.Parse(document).Ticket;
            Assert.Equal("platform-rebrand", ticket.ExtraFields["epic"]);

            Assert.Contains("- **epic:** platform-rebrand", TicketDocument.Serialize(ticket, "body"));
        }

        [Fact]
        public void Parse_DocumentHasABody_ReturnsItUntouched()
        {
            var body = "## Description\n\nA thing.\n\n## Notes\n\n- one\n- two";
            var parsed = TicketDocument.Parse(TicketDocument.Serialize(Sample(), body));

            Assert.Equal(body, parsed.Body);
        }

        [Fact]
        public void Parse_DocumentUsesWindowsLineEndings_ReadsItTheSame()
        {
            var parsed = TicketDocument.Parse("# NG-0001 — Something\r\n\r\n- **Type:** bug\r\n\r\nbody\r\n");

            Assert.Equal("NG-0001", parsed.Ticket.Id);
            Assert.Equal("Something", parsed.Ticket.Title);
            Assert.Equal("body", parsed.Body);
        }

        [Fact]
        public void Serialize_TitleSpansLines_CollapsesItSoTheHeadingStaysOneLine()
        {
            // A title is untrusted input. A newline in the heading would split the document rather
            // than fail, so the tail would silently become body.
            var ticket = Sample();
            ticket.Title = "WSJF index\ntool";

            var serialized = TicketDocument.Serialize(ticket, "body");

            Assert.StartsWith("# NG-0007 — WSJF index tool\n", serialized, StringComparison.Ordinal);
            Assert.Equal("WSJF index tool", TicketDocument.Parse(serialized).Ticket.Title);
        }

        [Fact]
        public void BuildInitialBody_Always_OmitsTheHeadingSerializeWrites()
        {
            var body = TicketDocument.BuildInitialBody(Sample(), "Something.");

            Assert.StartsWith("## Description", body, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(TicketDocument.Serialize(Sample(), body), "# NG-0007"));
        }

        [Fact]
        public void Parse_DocumentDoesNotOpenWithAHeading_ThrowsFormatException() =>
            Assert.Throws<FormatException>(() => TicketDocument.Parse("- **ID:** NG-0001\n\nbody"));

        [Fact]
        public void Parse_HeadingHasNoTitle_ThrowsFormatException() =>
            Assert.Throws<FormatException>(() => TicketDocument.Parse("# NG-0001\n\nbody"));

        [Fact]
        public void Parse_DocumentIsEmpty_ThrowsFormatException() =>
            Assert.Throws<FormatException>(() => TicketDocument.Parse("   \n\n"));

        [Fact]
        public void Parse_ScoreIsOffTheFibonacciScale_ThrowsFormatException() =>
            Assert.Throws<FormatException>(() => TicketDocument.Parse("# NG-1 — x\n\n- **bv:** 4\n\nbody"));

        [Fact]
        public void AppendActivity_BodyAlreadyHasAnActivityLog_AppendsUnderTheExistingHeading()
        {
            var body = TicketDocument.BuildInitialBody(Sample(), "Something.");
            var when = new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

            var updated = TicketDocument.AppendActivity(body, when, "started");

            Assert.Contains("- 2026-08-07 10:00 UTC — started", updated);
            Assert.Equal(1, CountOccurrences(updated, "## Activity Log"));
        }

        [Fact]
        public void AppendActivity_TimeZoneGiven_RendersTheEntryInThatZone()
        {
            // The log is prose for people and is never parsed back, so it gets the readable local
            // form. The offset is spelled out so it stays unambiguous.
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var when = new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

            var updated = TicketDocument.AppendActivity("body", when, "started", zone);

            Assert.Contains("2026-08-07 06:00 -04:00 — started", updated);
        }

        [Fact]
        public void AppendActivity_BodyHasNoActivityLog_CreatesTheHeading()
        {
            var updated = TicketDocument.AppendActivity("just a body", DateTimeOffset.UtcNow, "started");

            Assert.Contains("## Activity Log", updated);
            Assert.Contains("started", updated);
        }

        static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;

            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
