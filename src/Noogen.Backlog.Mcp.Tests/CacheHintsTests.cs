using ModelContextProtocol.Protocol;

namespace Noogen.Backlog.Mcp.Tests
{
    /// <summary>
    /// What discovery tells a caller about how long its answer keeps.
    ///
    /// The SDK's default is "immediately stale", so the value being non-zero is the whole point of
    /// the filter; and the scope has to stay public, because a shared gateway serving one copy to
    /// several callers is the case where caching this is worth anything at all.
    /// </summary>
    public class CacheHintsTests
    {
        [Fact]
        public void Apply_ACacheableResult_KeepsForTenMinutesAndIsNobodysInParticular()
        {
            var result = CacheHints.Apply(new ListToolsResult());

            Assert.Equal(TimeSpan.FromMinutes(10), result.TimeToLive);
            Assert.Equal(CacheScope.Public, result.CacheScope);
        }

        [Fact]
        public async Task On_AHandlerThatAnswers_StampsTheAnswerAndChangesNothingElse()
        {
            var answered = new ListToolsResult { NextCursor = "second-page" };
            var filter = CacheHints.On<ListToolsRequestParams, ListToolsResult>();

            // The context is never read — the filter stamps what came back and passes it on.
            var result = await filter((request, cancellationToken) => new ValueTask<ListToolsResult>(answered))(
                null!, CancellationToken.None);

            Assert.Same(answered, result);
            Assert.Equal("second-page", result.NextCursor);
            Assert.Equal(CacheHints.Lifetime, result.TimeToLive);
        }
    }
}
