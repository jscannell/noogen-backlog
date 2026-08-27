using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// How long a caller may hold what discovery told it, and who may hold it.
    ///
    /// Everything cacheable here is fixed for the life of the process and the same for everybody:
    /// one tool built at startup from <c>VerbCatalog</c>, and guides read out of the assembly. So
    /// the scope is public — there is no per-caller data in any of it, and a gateway in front of
    /// several callers should be able to answer them from one copy.
    ///
    /// The lifetime is not sized from that, though, because "static" is only true within a
    /// process. What it is sized from is a deploy: the verb list travels in the tool's schema as an
    /// enum, so a caller holding a stale one can refuse a verb that `help` — answered live, inside
    /// a result — has just told it about. Ten minutes is how long that disagreement is allowed to
    /// last. There is no shorter route: 2026-07-28 is stateless, so a restarted server cannot tell
    /// a caller that is not connected to it that the list changed.
    /// </summary>
    public static class CacheHints
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        public static T Apply<T>(T result) where T : ICacheableResult
        {
            result.TimeToLive = Lifetime;
            result.CacheScope = CacheScope.Public;

            return result;
        }

        /// <summary>
        /// The same, as a filter over one of the handlers whose result is cacheable. The SDK
        /// answers those from the collections the server was built with; this only stamps how long
        /// the answer keeps.
        /// </summary>
        public static McpRequestFilter<TParams, TResult> On<TParams, TResult>() where TResult : ICacheableResult =>
            next => async (request, cancellationToken) => Apply(await next(request, cancellationToken));
    }
}
