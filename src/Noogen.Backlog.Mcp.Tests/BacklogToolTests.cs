using System.Text.Json;
using System.Text.Json.Nodes;
using Noogen.Backlog.Tests;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Mcp.Tests
{
    /// <summary>
    /// What this front end decides, and what it must not.
    ///
    /// Two things are being held. The first is that an answer here is the same answer the CLI
    /// prints under <c>--json</c> — not a similar one, the same one — because that is the promise
    /// phase 1 was for and the only reason a caller can move between the two. The second is that a
    /// refusal is worth reading: over MCP the surface is not carried, it is asked for, so a wrong
    /// option is the moment a caller finds out what the right one is.
    /// </summary>
    public class BacklogToolTests
    {
        // --- the answer is the contract, not a shape of this front end's own ---

        [Fact]
        public async Task Invoke_List_AnswersWithExactlyTheJsonTheCommandLinePrints()
        {
            var backlog = await TestBacklog.CreateAsync();
            await backlog.AddAsync("First", bv: 20, size: 1);
            await backlog.AddAsync("Second", bv: 1, size: 20);

            var result = await TestServer.ToolFor(backlog).CallAsync("list");

            Assert.Equal(
                TestServer.Json(await backlog.Api.ListAsync(new TicketFilter())),
                result.Json());
        }

        [Fact]
        public async Task Invoke_ShowWithABody_AnswersWithExactlyTheJsonTheCommandLinePrints()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("A ticket");

            var result = await TestServer.ToolFor(backlog).CallAsync("show", TestServer.With("id", ticket.Id));

            Assert.Equal(
                TestServer.Json(await backlog.Api.ShowAsync(ticket.Id)),
                result.Json());
        }

        [Fact]
        public async Task Invoke_Wip_AnswersWithExactlyTheJsonTheCommandLinePrints()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Started");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            var result = await TestServer.ToolFor(backlog).CallAsync("wip");

            Assert.Equal(
                TestServer.Json(await backlog.Api.WipAsync(new TicketFilter())),
                result.Json());
        }

        [Fact]
        public async Task Invoke_FieldsNamed_NarrowsEveryTicketTheSameWayTheCommandLineDoes()
        {
            var backlog = await TestBacklog.CreateAsync();
            await backlog.AddAsync("First");

            var result = await TestServer.ToolFor(backlog).CallAsync("list", TestServer.With("fields", "id,title"));

            Assert.Equal(["id", "title"], TestServer.KeysOf(result.Structured()[0]).Order());
        }

        [Fact]
        public async Task Invoke_FieldNobodyDeclared_IsRefusedRatherThanQuietlyDropped()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("list", TestServer.With("fields", "id,frobnicate"));

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains("frobnicate", result.Error(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Invariant 13. There is no terminal on this path and no display setting to move with, so
        /// the two modifiers that exist for one do not exist here — and a caller reaching for them
        /// is told so rather than ignored.
        /// </summary>
        [Theory]
        [InlineData("json")]
        [InlineData("utc")]
        public async Task Invoke_JsonOrUtc_IsRefusedBecauseThereIsNothingElseToBe(string modifier)
        {
            var backlog = await TestBacklog.CreateAsync(timeZoneId: "America/New_York");

            var result = await TestServer.ToolFor(backlog).CallAsync("list", TestServer.With(modifier, true));

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains(modifier, result.Error(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_BacklogInAnotherTimezone_StillTimestampsInUtc()
        {
            var backlog = await TestBacklog.CreateAsync(timeZoneId: "America/New_York");
            var ticket = await backlog.AddAsync("A ticket");

            var result = await TestServer.ToolFor(backlog).CallAsync("show", TestServer.With("id", ticket.Id));
            var created = result.Structured().GetProperty("ticket").GetProperty("created").GetString();

            Assert.Equal(Iso.ToText(ticket.Created), created);
            Assert.EndsWith("Z", created, StringComparison.Ordinal);
        }

        // --- the surface, and getting it wrong ---

        [Fact]
        public async Task Invoke_VerbThatDoesNotExist_NamesTheOnesThatDo()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("lst");

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains("lst", result.Error(), StringComparison.Ordinal);

            foreach (var verb in VerbCatalog.On(VerbSurface.Mcp))
                Assert.Contains(verb.Name, result.Error(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_OptionTheVerbDoesNotRead_NamesWhatItDoesReadAndWhereToLook()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("list", TestServer.With("titel", "x"));

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains("titel", result.Error(), StringComparison.Ordinal);
            Assert.Contains("area", result.Error(), StringComparison.Ordinal);
            Assert.Contains("help", result.Error(), StringComparison.Ordinal);
        }

        /// <summary>
        /// AC #6. There is nowhere for a status to live and the answer to somebody reaching for one
        /// is not "that option does not exist" but "the verb you want is over there".
        /// </summary>
        [Theory]
        [InlineData("status")]
        [InlineData("phase")]
        public async Task Invoke_StatusOrPhaseOnAnEdit_PointsAtTheLifecycleVerbs(string option)
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("A ticket");

            var result = await TestServer.ToolFor(backlog).CallAsync("edit", new JsonObject
            {
                ["id"] = ticket.Id,
                [option] = "done"
            });

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains("the tab a ticket lives on is its state", result.Error(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_OptionValidationFails_TheBacklogIsNotTouched()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Original");

            await TestServer.ToolFor(backlog).CallAsync("edit", new JsonObject
            {
                ["id"] = ticket.Id,
                ["title"] = "Rewritten",
                ["stauts"] = "done"
            });

            var unchanged = await backlog.Store.GetAsync(ticket.Id);

            Assert.Equal("Original", unchanged!.Title);
        }

        /// <summary>
        /// A score has two spellings and a JSON object has one key. Silently letting the second win
        /// would lose a number the caller passed — and the object it arrived in cannot show that
        /// there had been two.
        /// </summary>
        [Fact]
        public async Task Invoke_OneOptionUnderBothItsSpellings_IsRefusedRatherThanLettingOneWin()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("A ticket");

            var result = await TestServer.ToolFor(backlog).CallAsync("score", new JsonObject
            {
                ["id"] = ticket.Id,
                ["bv"] = 2,
                ["business-value"] = 13
            });

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains("twice", result.Error(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_ScoreUnderItsLongSpelling_IsReadWhereTheShortOneWouldBe()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("A ticket", bv: null, tc: null, rroe: null, size: null);

            var result = await TestServer.ToolFor(backlog).CallAsync("score", new JsonObject
            {
                ["id"] = ticket.Id,
                ["business-value"] = 13,
                ["job-size"] = 1
            });

            Assert.Equal(13, result.Structured().GetProperty("bv").GetInt32());
            Assert.Equal(1, result.Structured().GetProperty("size").GetInt32());
        }

        [Theory]
        [InlineData("login")]
        [InlineData("logout")]
        [InlineData("init")]
        [InlineData("install-skill")]
        public async Task Invoke_VerbThatNeedsSomebodysOwnMachine_IsRefusedWithTheReason(string verb)
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync(verb);

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains(VerbCatalog.Require(verb).McpRefusal!, result.Error(), StringComparison.Ordinal);
        }

        // --- the argument a verb's usage shows in angle brackets ---

        [Fact]
        public async Task Invoke_TicketIdMissing_NamesTheOptionItArrivesUnder()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("show");

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains("'id'", result.Error(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_FindsByItsOwnPositionalName_ReadsTheSearchTextFromText()
        {
            var backlog = await TestBacklog.CreateAsync();
            await backlog.AddAsync("Rate limiting the Sheets client");

            var result = await TestServer.ToolFor(backlog).CallAsync("find", TestServer.With("text", "Sheets"));

            Assert.Single(result.Structured().EnumerateArray());
        }

        // --- refusals the backlog itself makes ---

        [Fact]
        public async Task Invoke_StartPastTheWipLimit_IsAResultRatherThanAProtocolError()
        {
            var backlog = await TestBacklog.CreateAsync(wipLimit: 1);
            var first = await backlog.AddAsync("First");
            var second = await backlog.AddAsync("Second");

            var tool = TestServer.ToolFor(backlog);

            await tool.CallAsync("start", TestServer.With("id", first.Id));
            var result = await tool.CallAsync("start", TestServer.With("id", second.Id));

            Assert.True(result.Failed());
            Assert.Equal(BacklogFault.WipLimit, result.Kind());
        }

        [Fact]
        public async Task Invoke_ForcedPastTheWipLimit_StartsAnyway()
        {
            var backlog = await TestBacklog.CreateAsync(wipLimit: 1);
            var first = await backlog.AddAsync("First");
            var second = await backlog.AddAsync("Second");

            var tool = TestServer.ToolFor(backlog);

            await tool.CallAsync("start", TestServer.With("id", first.Id));

            var result = await tool.CallAsync("start", new JsonObject
            {
                ["id"] = second.Id,
                ["force"] = true
            });

            Assert.False(result.Failed());
        }

        [Fact]
        public async Task Invoke_ScoreAfterWorkStarted_IsRefusedAsAnIllegalTransition()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("Started");
            await backlog.Store.StartAsync(ticket.Id, "jason", false);

            var result = await TestServer.ToolFor(backlog).CallAsync("score", new JsonObject
            {
                ["id"] = ticket.Id,
                ["bv"] = 13
            });

            Assert.Equal(BacklogFault.IllegalTransition, result.Kind());
        }

        [Fact]
        public async Task Invoke_TicketThatIsNotThere_IsRefusedAsNotFound()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("show", TestServer.With("id", "NG-9999"));

            Assert.Equal(BacklogFault.NotFound, result.Kind());
        }

        /// <summary>
        /// Invariant 9. A section ends at the next heading of its own level, so a `##` inside one is
        /// a sibling rather than part of it — and the damage only shows on the *second* write. It is
        /// refused before anything is written, here as everywhere.
        /// </summary>
        [Fact]
        public async Task Invoke_DescriptionCarryingASiblingHeading_IsRefusedBeforeTheTicketExists()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("new", new JsonObject
            {
                ["title"] = "A ticket",
                ["description"] = "Some prose.\n\n## Design\n\nMore prose."
            });

            Assert.Equal(BacklogFault.InvalidArgument, result.Kind());
            Assert.Contains("###", result.Error(), StringComparison.Ordinal);
            Assert.Empty(await backlog.Store.ListAsync(new TicketFilter()));
        }

        [Fact]
        public async Task Invoke_SectionGivenAsEmptyText_IsRefusedRatherThanClearingIt()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("A ticket");

            var result = await TestServer.ToolFor(backlog).CallAsync("edit", new JsonObject
            {
                ["id"] = ticket.Id,
                ["description"] = "   "
            });

            Assert.Equal(BacklogFault.Usage, result.Kind());
        }

        // --- prose, which is the reason this front end exists at all ---

        [Fact]
        public async Task Invoke_ProseWithNewlinesAndQuotes_ArrivesIntact()
        {
            var backlog = await TestBacklog.CreateAsync();
            var written = "The header said \"Retry-After: 8\".\n\n- [ ] It waits\n- [ ] It says so";

            var tool = TestServer.ToolFor(backlog);

            var filed = await tool.CallAsync("new", new JsonObject
            {
                ["title"] = "Honor Retry-After",
                ["acceptance-criteria"] = written
            });

            var id = filed.Structured().GetProperty("id").GetString()!;
            var shown = await tool.CallAsync("show", new JsonObject
            {
                ["id"] = id,
                ["section"] = "acceptance-criteria"
            });

            // The section comes back under its own heading, which is what a read-before-write wants.
            Assert.EndsWith(written, shown.Structured().GetProperty("body").GetString()!.Trim(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_NewWithNothingWritten_SaysWhichSectionsSayTodo()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("new", TestServer.With("title", "A ticket"));

            Assert.Contains("acceptance criteria", result.Text(), StringComparison.Ordinal);
            Assert.Contains("*TODO*", result.Text(), StringComparison.Ordinal);

            // And not in the structured half: a placeholder is something to tell somebody about,
            // not a field, and `new` answers with exactly what every other write answers with.
            Assert.Equal(["created", "docUrl", "id", "phase", "title", "type", "updated"],
                TestServer.KeysOf(result.Structured()).Order());
        }

        [Fact]
        public async Task Invoke_NoteMissingItsWords_NamesWhatIsMissing()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("A ticket");

            var result = await TestServer.ToolFor(backlog).CallAsync("note", TestServer.With("id", ticket.Id));

            Assert.Equal(BacklogFault.Usage, result.Kind());
            Assert.Contains("'text'", result.Error(), StringComparison.Ordinal);
        }

        // --- the lifecycle, which is the only way a ticket moves ---

        [Fact]
        public async Task Invoke_TheWholeLifecycle_MovesATicketFromQueueToArchive()
        {
            var backlog = await TestBacklog.CreateAsync();
            var tool = TestServer.ToolFor(backlog);

            var filed = await tool.CallAsync("new", new JsonObject
            {
                ["title"] = "Serve the verbs over MCP",
                ["area"] = "agent",
                ["bv"] = 13,
                ["tc"] = 5,
                ["rroe"] = 3,
                ["size"] = 5
            });

            var id = filed.Structured().GetProperty("id").GetString()!;

            Assert.False((await tool.CallAsync("start", TestServer.With("id", id))).Failed());
            Assert.False((await tool.CallAsync("block", new JsonObject { ["id"] = id, ["reason"] = "Waiting on the SDK." })).Failed());
            Assert.False((await tool.CallAsync("unblock", TestServer.With("id", id))).Failed());
            Assert.False((await tool.CallAsync("review", TestServer.With("id", id))).Failed());

            var archived = await tool.CallAsync("archive", new JsonObject { ["id"] = id, ["as"] = "done" });

            Assert.Equal("done", archived.Structured().GetProperty("outcome").GetString());
            Assert.Equal(BacklogPhase.Archive, (await backlog.Store.GetAsync(id))!.Phase);
        }

        [Fact]
        public async Task Invoke_StartWithNoOwner_AttributesItToTheIdentityTheServerActsAs()
        {
            var backlog = await TestBacklog.CreateAsync();
            var ticket = await backlog.AddAsync("A ticket");

            var result = await TestServer.ToolFor(backlog).CallAsync("start", TestServer.With("id", ticket.Id));

            Assert.Equal(TestServer.Owner, result.Structured().GetProperty("owner").GetString());
        }

        [Fact]
        public async Task Invoke_NewWithNoOwner_LeavesItUnownedRatherThanStampingTheServerOnIt()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("new", TestServer.With("title", "A ticket"));

            Assert.False(result.Structured().TryGetProperty("owner", out _));
        }

        // --- what this server is ---

        [Fact]
        public async Task Invoke_WhoAmI_SaysWhoAWriteIsAttributedToAndThatItIsNotYou()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("whoami");
            var identity = result.Structured();

            Assert.Equal(TestServer.Owner, identity.GetProperty("owner").GetString());
            Assert.True(identity.GetProperty("sharedIdentity").GetBoolean());
        }

        /// <summary>
        /// The credential's description names a key file's path on the server's filesystem, or the
        /// operator's own address and keystore; the spreadsheet id is the handle to a whole
        /// backlog. A caller has no business with any of it, and under multi-tenancy it would be
        /// one tenant reading another's configuration. It goes to the startup log instead.
        /// </summary>
        [Fact]
        public async Task Invoke_WhoAmI_TellsTheCallerNothingAboutHowTheServerIsConfigured()
        {
            var backlog = await TestBacklog.CreateAsync();
            var tool = TestServer.ToolFor(backlog);

            var result = await tool.CallAsync("whoami");

            Assert.Equal(["owner", "sharedIdentity"], TestServer.KeysOf(result.Structured()).Order());
            Assert.DoesNotContain("/etc/noogen", result.Text(), StringComparison.Ordinal);
            Assert.DoesNotContain(TestBacklog.SpreadsheetId, result.Text(), StringComparison.Ordinal);
        }

        // --- the disclosure ladder ---

        [Fact]
        public async Task Invoke_HelpWithNothing_NamesEveryVerbThisServerOffers()
        {
            var backlog = await TestBacklog.CreateAsync();

            var help = (await TestServer.ToolFor(backlog).CallAsync("help")).Text();

            foreach (var verb in VerbCatalog.On(VerbSurface.Mcp))
                Assert.Contains(verb.Name, help, StringComparison.Ordinal);

            Assert.DoesNotContain("--json", help, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_HelpWithAVerb_NamesEveryOptionThatVerbReads()
        {
            var backlog = await TestBacklog.CreateAsync();
            var tool = TestServer.ToolFor(backlog);

            foreach (var verb in VerbCatalog.On(VerbSurface.Mcp))
            {
                var help = (await tool.CallAsync("help", TestServer.With("verb", verb.Name))).Text();

                Assert.Contains(verb.Summary, help, StringComparison.Ordinal);

                foreach (var name in VerbArguments.Accepted(verb))
                    Assert.Contains(name, help, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Invoke_HelpWithAWithheldVerb_AnswersWithTheReasonRatherThanUsage()
        {
            var backlog = await TestBacklog.CreateAsync();

            var help = (await TestServer.ToolFor(backlog).CallAsync("help", TestServer.With("verb", "login"))).Text();

            Assert.Contains(VerbCatalog.Require("login").McpRefusal!, help, StringComparison.Ordinal);
            Assert.DoesNotContain("usage:", help, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Invoke_HelpWithATopic_ReadsTheGuideWhole()
        {
            var backlog = await TestBacklog.CreateAsync();

            var guide = (await TestServer.ToolFor(backlog).CallAsync("help", TestServer.With("topic", "wsjf"))).Text();

            Assert.Equal(BacklogGuide.Read("wsjf"), guide);
        }

        [Fact]
        public async Task Invoke_HelpWithBothAVerbAndATopic_RefusesRatherThanChoosing()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("help", new JsonObject
            {
                ["verb"] = "show",
                ["topic"] = "wsjf"
            });

            Assert.Equal(BacklogFault.Usage, result.Kind());
        }

        [Fact]
        public async Task Invoke_HelpWithATopicThatIsNotAGuide_NamesTheOnesThatAre()
        {
            var backlog = await TestBacklog.CreateAsync();

            var result = await TestServer.ToolFor(backlog).CallAsync("help", TestServer.With("topic", "wsfj"));

            Assert.Equal(BacklogFault.Usage, result.Kind());

            foreach (var topic in VerbCatalog.Guides)
                Assert.Contains(topic, result.Error(), StringComparison.Ordinal);
        }

        // --- the tool as the server offers it ---

        [Fact]
        public async Task Describe_TheTool_CarriesTheVerbListInItsSchemaSoATypoNeverArrives()
        {
            var backlog = await TestBacklog.CreateAsync();
            var described = BacklogTool.Describe(TestServer.ToolFor(backlog));

            var verbs = described.ProtocolTool.InputSchema
                .GetProperty("properties")
                .GetProperty("verb")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToList();

            Assert.Equal([.. VerbCatalog.On(VerbSurface.Mcp).Select(verb => verb.Name)], verbs);
        }

        [Fact]
        public async Task Describe_TheTool_TakesOptionsAsAFreeFormObject()
        {
            var backlog = await TestBacklog.CreateAsync();
            var described = BacklogTool.Describe(TestServer.ToolFor(backlog));

            var options = described.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("options");

            Assert.Equal(JsonValueKind.Object, options.ValueKind);
            Assert.False(options.TryGetProperty("properties", out _));
        }

        /// <summary>
        /// The description is a discovery surface, not a summary: it is what a model weighs when
        /// somebody says "create a ticket", against every other tool it has. A caller reaching this
        /// server has no skill installed, so the words that would have matched the skill's
        /// frontmatter have to be here instead.
        /// </summary>
        [Theory]
        [InlineData("ticket")]
        [InlineData("backlog")]
        [InlineData("Create")]
        [InlineData("work on next")]
        public async Task Describe_TheTool_CarriesTheWordsSomebodyWouldAskWith(string word)
        {
            var backlog = await TestBacklog.CreateAsync();

            Assert.Contains(
                word,
                BacklogTool.Describe(TestServer.ToolFor(backlog)).ProtocolTool.Description ?? string.Empty,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// One tool, because a 2026-07-28 server's tool list may not vary per connection: a surface
        /// that cannot be unlocked after discovery has to disclose itself from inside a result.
        /// </summary>
        [Fact]
        public async Task Describe_TheTool_IsTheOnlyOne()
        {
            var backlog = await TestBacklog.CreateAsync();

            Assert.Equal(BacklogTool.ToolName, BacklogTool.Describe(TestServer.ToolFor(backlog)).ProtocolTool.Name);
        }

        /// <summary>
        /// Every verb the catalog offers here has to be wired to something, or the surface
        /// describes a call this server refuses to make.
        /// </summary>
        [Fact]
        public async Task Invoke_EveryVerbTheCatalogOffers_IsImplemented()
        {
            var backlog = await TestBacklog.CreateAsync();
            var tool = TestServer.ToolFor(backlog);

            foreach (var verb in VerbCatalog.On(VerbSurface.Mcp))
            {
                var result = await tool.CallAsync(verb.Name);

                Assert.DoesNotContain("declared but not implemented", result.Failed() ? result.Error() : string.Empty,
                    StringComparison.Ordinal);
            }
        }
    }
}
