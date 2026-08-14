namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// The reading half of the section machinery: what `show` narrows to, and what it leaves out.
    ///
    /// These share <c>FindSection</c> with <see cref="TicketSectionTests"/> deliberately — a reader
    /// that disagreed with the writer about where a section ends would hand back text that a
    /// replacement then would not overwrite, and the caller would lose the difference without
    /// seeing it. So the boundary cases are pinned on both sides.
    ///
    /// The trim is display only. The last test here is the one that matters: it must never be
    /// possible for a trimmed body to be what gets stored.
    /// </summary>
    public class TicketSectionReadTests
    {
        const string Body =
            "## Description\n\nThe original description.\n\n" +
            "## Acceptance Criteria\n\n- [ ] *TODO*\n\n" +
            "## Notes\n\nSomething a person wrote.\n\n" +
            "## Activity Log\n\n" +
            "- 2026-08-01 09:00 — created\n" +
            "- 2026-08-02 09:00 — started\n" +
            "- 2026-08-03 09:00 — blocked\n" +
            "- 2026-08-04 09:00 — unblocked\n" +
            "- 2026-08-05 09:00 — in review\n";

        // --- SectionOf ---

        [Fact]
        public void SectionOf_SectionExists_ReturnsItsHeadingAndText()
        {
            Assert.Equal(
                "## Description\n\nThe original description.\n",
                TicketDocument.SectionOf(Body, TicketDocument.DescriptionHeading));
        }

        [Fact]
        public void SectionOf_SectionExists_StopsAtTheNextHeadingOfTheSameLevel()
        {
            var section = TicketDocument.SectionOf(Body, TicketDocument.AcceptanceCriteriaHeading);

            Assert.Equal("## Acceptance Criteria\n\n- [ ] *TODO*\n", section);
            Assert.DoesNotContain("Notes", section, StringComparison.Ordinal);
        }

        /// <summary>
        /// The same rule <c>ReplaceSection</c> follows: a deeper heading is inside the section, so
        /// reading it back gives exactly what a replacement would overwrite.
        /// </summary>
        [Fact]
        public void SectionOf_SectionHasASubheading_KeepsItInsideTheSection()
        {
            var body = "## Description\n\nIntro.\n\n### Background\n\nMore.\n\n## Notes\n\nKeep me.\n";

            var section = TicketDocument.SectionOf(body, TicketDocument.DescriptionHeading);

            Assert.Equal("## Description\n\nIntro.\n\n### Background\n\nMore.\n", section);
        }

        [Fact]
        public void SectionOf_SectionIsLast_ReadsToTheEndOfTheBody()
        {
            Assert.Equal(
                "## Notes\n\nKeep me.\n",
                TicketDocument.SectionOf("## Description\n\nOld.\n\n## Notes\n\nKeep me.\n", TicketDocument.NotesHeading));
        }

        [Fact]
        public void SectionOf_HeadingIsMissing_ReturnsNull()
        {
            Assert.Null(TicketDocument.SectionOf("## Notes\n\nKeep me.\n", TicketDocument.DescriptionHeading));
        }

        [Fact]
        public void SectionOf_HeadingDiffersInCase_StillMatchesIt()
        {
            Assert.Equal(
                "## DESCRIPTION\n\nOld.\n",
                TicketDocument.SectionOf("## DESCRIPTION\n\nOld.\n\n## Notes\n\nKeep me.\n", TicketDocument.DescriptionHeading));
        }

        /// <summary>
        /// Docs' export escapes punctuation (invariant 18). A reader that tidied it would hand back
        /// something a replacement could not round-trip.
        /// </summary>
        [Fact]
        public void SectionOf_SectionCarriesDocsEscaping_ReturnsItVerbatim()
        {
            var body = "## Description\n\nThe sign\\-in flow, and a \\-\\- dash.\n\n## Notes\n\nKeep me.\n";

            Assert.Equal(
                "## Description\n\nThe sign\\-in flow, and a \\-\\- dash.\n",
                TicketDocument.SectionOf(body, TicketDocument.DescriptionHeading));
        }

        /// <summary>
        /// Read then write is the whole point of the option, so it has to be a no-op when the text
        /// has not changed.
        /// </summary>
        [Fact]
        public void SectionOf_TextIsWrittenBackUnchanged_LeavesTheDocumentAlone()
        {
            var section = TicketDocument.SectionOf(Body, TicketDocument.DescriptionHeading)!;
            var text = section["## Description\n\n".Length..].TrimEnd('\n');

            Assert.Equal(Body, TicketDocument.ReplaceSection(Body, TicketDocument.DescriptionHeading, text));
        }

        // --- TrimActivityLog ---

        [Fact]
        public void TrimActivityLog_MoreEntriesThanKept_KeepsTheMostRecentOnes()
        {
            var trimmed = TicketDocument.TrimActivityLog(Body, 2);

            Assert.Contains("- 2026-08-04 09:00 — unblocked", trimmed, StringComparison.Ordinal);
            Assert.Contains("- 2026-08-05 09:00 — in review", trimmed, StringComparison.Ordinal);
            Assert.DoesNotContain("- 2026-08-01 09:00 — created", trimmed, StringComparison.Ordinal);
        }

        [Fact]
        public void TrimActivityLog_MoreEntriesThanKept_SaysHowManyItDropped()
        {
            var trimmed = TicketDocument.TrimActivityLog(Body, 2);

            Assert.Contains("3 earlier entries", trimmed, StringComparison.Ordinal);
            Assert.Contains("--full", trimmed, StringComparison.Ordinal);
        }

        [Fact]
        public void TrimActivityLog_OneEntryDropped_SaysEntryNotEntries()
        {
            Assert.Contains("1 earlier entry;", TicketDocument.TrimActivityLog(Body, 4), StringComparison.Ordinal);
        }

        [Fact]
        public void TrimActivityLog_FewerEntriesThanKept_LeavesTheBodyAlone()
        {
            Assert.Equal(Body, TicketDocument.TrimActivityLog(Body, 10));
        }

        [Fact]
        public void TrimActivityLog_ExactlyAsManyEntriesAsKept_LeavesTheBodyAlone()
        {
            Assert.Equal(Body, TicketDocument.TrimActivityLog(Body, 5));
        }

        [Fact]
        public void TrimActivityLog_BodyHasNoActivityLog_LeavesItAlone()
        {
            var body = "## Description\n\nOld.\n\n## Notes\n\nKeep me.\n";

            Assert.Equal(body, TicketDocument.TrimActivityLog(body, 3));
        }

        [Fact]
        public void TrimActivityLog_AlwaysLeavesEverySectionAboveTheLogUntouched()
        {
            var trimmed = TicketDocument.TrimActivityLog(Body, 1);

            Assert.Equal(
                Body[..Body.IndexOf("## Activity Log", StringComparison.Ordinal)],
                trimmed[..trimmed.IndexOf("## Activity Log", StringComparison.Ordinal)]);
        }

        /// <summary>
        /// Docs puts two trailing spaces on every list item but the last, and a person may wrap a
        /// long note. A continuation line does not start a new entry, so counting by "- " alone
        /// would cut an entry in half and attribute its tail to the one before it.
        /// </summary>
        [Fact]
        public void TrimActivityLog_EntrySpansLines_KeepsTheWholeEntry()
        {
            var body =
                "## Activity Log\n\n" +
                "- 2026-08-01 09:00 — created\n" +
                "- 2026-08-02 09:00 — a long note\n  that wrapped onto a second line\n" +
                "- 2026-08-03 09:00 — done\n";

            var trimmed = TicketDocument.TrimActivityLog(body, 2);

            Assert.Contains("a long note\n  that wrapped onto a second line", trimmed, StringComparison.Ordinal);
            Assert.DoesNotContain("created", trimmed, StringComparison.Ordinal);
        }

        /// <summary>
        /// The log is the only prose record of a ticket's life. Trimming is a view; feeding the
        /// result back into a write would delete history that nothing else holds, which is the
        /// unrecoverable failure invariant 9 exists to prevent.
        /// </summary>
        [Fact]
        public void TrimActivityLog_Result_IsNeverWhatAWriteWouldStore()
        {
            var trimmed = TicketDocument.TrimActivityLog(Body, 1);

            Assert.NotEqual(Body, trimmed);
            Assert.DoesNotContain("- 2026-08-01 09:00 — created", trimmed, StringComparison.Ordinal);

            // Editing the stored body — the only thing a write ever sees — keeps every entry.
            var edited = TicketDocument.ReplaceSection(Body, TicketDocument.DescriptionHeading, "New.");

            Assert.Contains("- 2026-08-01 09:00 — created", edited, StringComparison.Ordinal);
            Assert.Contains("- 2026-08-05 09:00 — in review", edited, StringComparison.Ordinal);
        }

        // --- HeadingsOf ---

        [Fact]
        public void HeadingsOf_BodyHasSections_ReturnsThemInOrder()
        {
            Assert.Equal(
                ["Description", "Acceptance Criteria", "Notes", "Activity Log"],
                TicketDocument.HeadingsOf(Body));
        }

        [Fact]
        public void HeadingsOf_BodyHasASubheading_ReturnsItToo()
        {
            Assert.Equal(
                ["Description", "Background"],
                TicketDocument.HeadingsOf("## Description\n\nIntro.\n\n### Background\n\nMore.\n"));
        }

        [Fact]
        public void HeadingsOf_BodyHasNoHeadings_ReturnsNothing()
        {
            Assert.Empty(TicketDocument.HeadingsOf("Just some prose someone typed.\n"));
        }
    }
}
