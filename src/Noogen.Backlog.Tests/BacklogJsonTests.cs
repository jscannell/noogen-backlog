using System.Text.Json;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// `--json` is the agent contract, and an agent pays for every byte of it. These pin the two
    /// things that keep it cheap without changing what it means: no indentation, and `--fields` to
    /// ask for less. Both must leave the *shapes* exactly as they were — same names, same values,
    /// and absent still meaning absent.
    /// </summary>
    public class BacklogJsonTests
    {
        static Ticket Sample() => new()
        {
            Id = "NG-0012",
            Title = "Fix the sign-in flow",
            Type = TicketType.Bug,
            Phase = BacklogPhase.Backlog,
            Area = "cli",
            Owner = "j@noogen.ai",
            Score = new WsjfScore { BusinessValue = 8, TimeCriticality = 5, RiskReductionOpportunityEnablement = 2, JobSize = 1 },
            Created = new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.Zero),
            Updated = new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.Zero)
        };

        static string Serialize(object payload)
        {
            var writer = new StringWriter();
            var previous = Console.Out;

            try
            {
                Console.SetOut(writer);
                Console.Out.Write(BacklogJson.Serialize(payload));
            }
            finally
            {
                Console.SetOut(previous);
            }

            return writer.ToString().TrimEnd();
        }

        [Fact]
        public void WriteJson_AnyPayload_IsNotIndented()
        {
            var json = Serialize(TicketView.From(Sample()));

            Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
            Assert.Contains("\"id\":\"NG-0012\"", json, StringComparison.Ordinal);
        }

        [Fact]
        public void WriteJson_AnyPayload_StillParsesToTheSameShape()
        {
            using var document = JsonDocument.Parse(Serialize(TicketView.From(Sample())));

            Assert.Equal("NG-0012", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("Fix the sign-in flow", document.RootElement.GetProperty("title").GetString());
            Assert.Equal(8, document.RootElement.GetProperty("bv").GetInt32());
        }

        /// <summary>A title with a dash and an owner with an @ must not come back as \uXXXX noise.</summary>
        [Fact]
        public void WriteJson_ValueNeedsNoHtmlEscaping_LeavesItReadable()
        {
            var json = Serialize(TicketView.From(Sample()));

            Assert.Contains("j@noogen.ai", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
        }

        // --- search results ---

        /// <summary>
        /// Which source matched is part of the answer: a name hit is exact and current, a body hit
        /// came from an index that lags and matches whole words.
        /// </summary>
        [Fact]
        public void From_MatchHitBothSources_CarriesBothOnTheWire()
        {
            var match = new TicketMatch { Ticket = Sample(), InName = true, InBody = true };

            using var document = JsonDocument.Parse(Serialize(TicketView.From(match)));

            Assert.Equal(
                ["name", "body"],
                document.RootElement.GetProperty("match").EnumerateArray().Select(element => element.GetString()).ToList());
        }

        /// <summary>
        /// A ticket does not have a match; a search result does. Every other verb must keep
        /// emitting the shape it emitted before search existed.
        /// </summary>
        [Fact]
        public void From_PlainTicket_HasNoMatchField()
        {
            using var document = JsonDocument.Parse(Serialize(TicketView.From(Sample())));

            Assert.False(document.RootElement.TryGetProperty("match", out _));
        }

        [Fact]
        public void Project_MatchIsNamed_IsSelectableLikeAnyOtherField()
        {
            var view = TicketView.From(new TicketMatch { Ticket = Sample(), InBody = true });

            using var document = JsonDocument.Parse(view.ToNode(BacklogJson.ParseFields("id,match")).ToJsonString());

            Assert.Equal(
                ["id", "match"],
                document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToList());
        }

        // --- --fields ---

        [Fact]
        public void ParseFields_NoValue_ReturnsNullMeaningEverything()
        {
            Assert.Null(BacklogJson.ParseFields(null));
            Assert.Null(BacklogJson.ParseFields("  "));
        }

        [Fact]
        public void Project_FieldsAreNamed_KeepsOnlyThose()
        {
            var fields = BacklogJson.ParseFields("id,wsjf,title");

            var json = TicketView.From(Sample()).ToNode(fields).ToJsonString();

            using var document = JsonDocument.Parse(json);

            Assert.Equal(
                ["id", "title", "wsjf"],
                document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToList());
        }

        [Fact]
        public void Project_NoFields_KeepsEverything()
        {
            var ticket = Sample();
            ticket.DocUrl = "https://docs.google.com/document/d/abc/edit";

            using var document = JsonDocument.Parse(TicketView.From(ticket).ToNode(null).ToJsonString());

            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();

            Assert.Contains("docUrl", names, StringComparer.Ordinal);
            Assert.Contains("id", names, StringComparer.Ordinal);
            Assert.Contains("wsjf", names, StringComparer.Ordinal);
        }

        [Fact]
        public void Project_FieldsAreNamed_KeepsTheSameValuesAsAnUnprojectedResponse()
        {
            var view = TicketView.From(Sample());

            using var projected = JsonDocument.Parse(view.ToNode(BacklogJson.ParseFields("id,bv")).ToJsonString());
            using var whole = JsonDocument.Parse(view.ToNode(null).ToJsonString());

            Assert.Equal(whole.RootElement.GetProperty("id").GetString(), projected.RootElement.GetProperty("id").GetString());
            Assert.Equal(whole.RootElement.GetProperty("bv").GetInt32(), projected.RootElement.GetProperty("bv").GetInt32());
        }

        /// <summary>
        /// Null-elision is the contract: absent means absent. Asking for a field this ticket does
        /// not carry must not conjure a null into a response that would not have had one.
        /// </summary>
        [Fact]
        public void Project_FieldIsAbsentOnThisTicket_StaysAbsentRatherThanBecomingNull()
        {
            var json = TicketView.From(Sample()).ToNode(BacklogJson.ParseFields("id,startedAt")).ToJsonString();

            using var document = JsonDocument.Parse(json);

            Assert.True(document.RootElement.TryGetProperty("id", out _));
            Assert.False(document.RootElement.TryGetProperty("startedAt", out _));
        }

        [Fact]
        public void ParseFields_NameIsMisspelled_RefusesAndListsTheRealOnes()
        {
            var exception = Assert.Throws<UsageException>(() => BacklogJson.ParseFields("id,wsfj"));

            Assert.Contains("'wsfj'", exception.Message, StringComparison.Ordinal);
            Assert.Contains("wsjf", exception.Message, StringComparison.Ordinal);
            Assert.Contains("title", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ParseFields_NameDiffersInCase_IsAccepted()
        {
            var json = TicketView.From(Sample()).ToNode(BacklogJson.ParseFields("ID,Title")).ToJsonString();

            Assert.Contains("\"id\":", json, StringComparison.Ordinal);
            Assert.Contains("\"title\":", json, StringComparison.Ordinal);
        }

        [Fact]
        public void ParseFields_ValueHasSpacesAndATrailingComma_ReadsThemAnyway()
        {
            var json = TicketView.From(Sample()).ToNode(BacklogJson.ParseFields(" id , title , ")).ToJsonString();

            using var document = JsonDocument.Parse(json);

            Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        }
    }
}
