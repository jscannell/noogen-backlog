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
        public void Round_trips_every_field()
        {
            var original = Sample();
            var serialized = TicketDocument.Serialize(original, "## Description\n\nSomething.");

            var parsed = TicketDocument.Parse(serialized).Ticket;

            Assert.Equal(original.Id, parsed.Id);
            Assert.Equal(original.Title, parsed.Title);
            Assert.Equal(original.Type, parsed.Type);
            Assert.Equal(original.Area, parsed.Area);
            Assert.Equal(original.Owner, parsed.Owner);
            Assert.Equal(original.Phase, parsed.Phase);
            Assert.Equal(original.State, parsed.State);
            Assert.Equal(original.BlockedReason, parsed.BlockedReason);
            Assert.Equal(original.BlockedAt, parsed.BlockedAt);
            Assert.Equal(original.StartedAt, parsed.StartedAt);
            Assert.Equal(original.Created, parsed.Created);
            Assert.Equal(original.Updated, parsed.Updated);
            Assert.Equal(original.Score.BusinessValue, parsed.Score.BusinessValue);
            Assert.Equal(original.Score.JobSize, parsed.Score.JobSize);
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

            Assert.Contains("- 2026-08-07T10:00:00Z — started", updated);
            Assert.Equal(1, CountOccurrences(updated, "## Activity Log"));
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
