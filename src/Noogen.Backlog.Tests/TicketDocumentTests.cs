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
        public void Round_trips_every_human_editable_field()
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
        public void Omits_machine_bookkeeping_a_human_should_not_hand_maintain()
        {
            // Timestamps, phase, and work state live in the Sheet and in Drive's file metadata.
            // Duplicating them here would mean a person hand-editing ISO-8601, and a stale copy
            // is worse than no copy. The Activity Log carries the same story in prose.
            var serialized = TicketDocument.Serialize(Sample(), "body");

            Assert.DoesNotContain("created:", serialized);
            Assert.DoesNotContain("updated:", serialized);
            Assert.DoesNotContain("started_at:", serialized);
            Assert.DoesNotContain("blocked_at:", serialized);
            Assert.DoesNotContain("archived_at:", serialized);
            Assert.DoesNotContain("phase:", serialized);
            Assert.DoesNotContain("state:", serialized);
        }

        [Fact]
        public void Still_reads_a_legacy_document_that_carries_the_old_fields()
        {
            var legacy = """
                ---
                id: NG-0001
                title: Something
                phase: in-progress
                state: blocked
                started_at: 2026-08-05T09:00:00Z
                created: 2026-08-01T08:00:00Z
                ---

                body
                """;

            var ticket = TicketDocument.Parse(legacy).Ticket;

            Assert.Equal(BacklogPhase.InProgress, ticket.Phase);
            Assert.Equal(WorkState.Blocked, ticket.State);
            Assert.Equal(new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero), ticket.StartedAt);

            // Read, but not written back — and not smuggled through ExtraFields either.
            Assert.DoesNotContain("phase:", TicketDocument.Serialize(ticket, "body"));
        }

        [Fact]
        public void Round_trip_is_stable_a_second_time()
        {
            var once = TicketDocument.Serialize(Sample(), "body");
            var twice = TicketDocument.Serialize(TicketDocument.Parse(once).Ticket, TicketDocument.Parse(once).Body);

            Assert.Equal(once, twice);
        }

        [Fact]
        public void Preserves_unknown_fields_a_human_added()
        {
            var document = """
                ---
                id: NG-0001
                title: Something
                epic: platform-rebrand
                ---

                body
                """;

            var ticket = TicketDocument.Parse(document).Ticket;
            Assert.Equal("platform-rebrand", ticket.ExtraFields["epic"]);

            var round = TicketDocument.Serialize(ticket, "body");
            Assert.Contains("epic: platform-rebrand", round);
        }

        [Fact]
        public void Body_survives_untouched()
        {
            var body = "## Description\n\nA thing.\n\n## Notes\n\n- one\n- two";
            var parsed = TicketDocument.Parse(TicketDocument.Serialize(Sample(), body));

            Assert.Equal(body, parsed.Body);
        }

        [Fact]
        public void Handles_windows_line_endings()
        {
            var parsed = TicketDocument.Parse("---\r\nid: NG-0001\r\ntitle: Something\r\n---\r\n\r\nbody\r\n");

            Assert.Equal("NG-0001", parsed.Ticket.Id);
            Assert.Equal("body", parsed.Body);
        }

        [Fact]
        public void Rejects_a_missing_opening_delimiter() =>
            Assert.Throws<FormatException>(() => TicketDocument.Parse("id: NG-0001\n\nbody"));

        [Fact]
        public void Rejects_unclosed_frontmatter() =>
            Assert.Throws<FormatException>(() => TicketDocument.Parse("---\nid: NG-0001\ntitle: x\n"));

        [Fact]
        public void Rejects_a_document_with_no_id() =>
            Assert.Throws<FormatException>(() => TicketDocument.Parse("---\ntitle: x\n---\n\nbody"));

        [Fact]
        public void Rejects_an_off_scale_score() =>
            Assert.Throws<FormatException>(() => TicketDocument.Parse("---\nid: NG-1\ntitle: x\nbv: 4\n---\n\nbody"));

        [Fact]
        public void Appends_activity_under_the_existing_heading()
        {
            var body = TicketDocument.BuildInitialBody(Sample(), "Something.");
            var when = new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

            var updated = TicketDocument.AppendActivity(body, when, "started");

            Assert.Contains("- 2026-08-07 10:00 UTC — started", updated);
            Assert.Equal(1, CountOccurrences(updated, "## Activity Log"));
        }

        [Fact]
        public void Activity_entries_render_in_the_configured_timezone()
        {
            // The log is prose for people and is never parsed back, so it gets the readable local
            // form. The offset is spelled out so it stays unambiguous.
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var when = new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

            var updated = TicketDocument.AppendActivity("body", when, "started", zone);

            Assert.Contains("2026-08-07 06:00 -04:00 — started", updated);
        }

        [Fact]
        public void Creates_the_activity_heading_when_absent()
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
