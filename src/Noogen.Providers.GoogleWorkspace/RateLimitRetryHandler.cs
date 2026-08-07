using System.Net;
using System.Text.Json;
using Google;
using Google.Apis.Http;
using Google.Apis.Services;

namespace Noogen.Providers.GoogleWorkspace
{
    /// <summary>
    /// Waits between attempts, and supplies the randomness that keeps two machines retrying the
    /// same rate-limited backlog from marching in step. Injected so tests assert the wait instead
    /// of serving it.
    /// </summary>
    public interface IRetryScheduler
    {
        /// <summary>A value in [0, 1) used to jitter a computed delay.</summary>
        double NextFraction();

        Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken);
    }

    public class RetryScheduler : IRetryScheduler
    {
        public double NextFraction() => Random.Shared.NextDouble();

        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            Task.Delay(duration, cancellationToken);
    }

    /// <summary>
    /// Told before each wait. A retry is the one thing that makes a command look hung, so the
    /// shell above gets the chance to say why it is quiet.
    /// </summary>
    public interface IRetryListener
    {
        void RateLimited(int attempt, int maxAttempts, TimeSpan delay);
    }

    /// <summary>
    /// Retries a request Google refused for rate — 429, and the older 403 spelling of the same
    /// thing — with exponential backoff, honouring <c>Retry-After</c> when Google sends one.
    ///
    /// This is safe to do to any verb, including the appends and deletes of a phase transition:
    /// a rate-limit response means the request was *rejected*, never half-applied, so retrying it
    /// cannot duplicate a row. That is the opposite of a timeout, which is why nothing here
    /// retries one.
    ///
    /// The library already backs off on 503 and on transport exceptions
    /// (<see cref="Google.Apis.Services.BaseClientService.Initializer.DefaultExponentialBackOffPolicy"/>);
    /// quota is the gap this fills.
    /// </summary>
    public class RateLimitRetryHandler : IHttpUnsuccessfulResponseHandler
    {
        /// <summary>One attempt plus four retries — about fifteen seconds of waiting at worst.</summary>
        public const int DefaultMaxAttempts = 5;

        internal static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Caps a wait we were told to take. Only reachable through <c>Retry-After</c>; the
        /// computed backoff tops out well below it.
        /// </summary>
        internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

        readonly IRetryScheduler _scheduler;
        readonly IRetryListener? _listener;
        readonly int _maxAttempts;

        public RateLimitRetryHandler(IRetryListener? listener = null, IRetryScheduler? scheduler = null, int maxAttempts = DefaultMaxAttempts)
        {
            if (maxAttempts < 1 || maxAttempts > ConfigurableMessageHandler.MaxAllowedNumTries)
                throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, $"Attempts must be between 1 and {ConfigurableMessageHandler.MaxAllowedNumTries}.");

            _listener = listener;
            _scheduler = scheduler ?? new RetryScheduler();
            _maxAttempts = maxAttempts;
        }

        public int MaxAttempts => _maxAttempts;

        /// <summary>
        /// The retry loop lives in the message handler, and it stops at <c>NumTries</c> — which
        /// defaults to 3 — no matter what this handler returns. Raising it is what makes the
        /// backoff above reachable.
        /// </summary>
        public void Attach(BaseClientService service)
        {
            var handler = service.HttpClient.MessageHandler;

            if (handler.NumTries < _maxAttempts)
                handler.NumTries = _maxAttempts;

            handler.AddUnsuccessfulResponseHandler(this);
        }

        public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            // The message handler's own budget, which Attach raised. Both limits are checked
            // because either can be the smaller one: a service built elsewhere may allow fewer
            // tries than this handler would take.
            if (!args.SupportsRetry || args.CurrentFailedTry >= _maxAttempts)
                return false;

            if (!await IsRateLimitedAsync(args.Response))
                return false;

            var delay = Delay(args.CurrentFailedTry, RetryAfter(args.Response), _scheduler.NextFraction());

            _listener?.RateLimited(args.CurrentFailedTry, _maxAttempts, delay);
            await _scheduler.WaitAsync(delay, args.CancellationToken);

            return true;
        }

        /// <summary>
        /// Equal jitter: half the backoff is fixed, half is spread. Full jitter can return a wait
        /// of nearly nothing, which against a per-minute quota is just another rejection.
        /// </summary>
        internal static TimeSpan Delay(int failedTries, TimeSpan? retryAfter, double fraction)
        {
            if (retryAfter.HasValue)
                return Clamp(retryAfter.Value);

            var doublings = Math.Min(failedTries - 1, 16);
            var backoff = Clamp(BaseDelay * Math.Pow(2, doublings));

            return backoff / 2 + backoff * Math.Clamp(fraction, 0, 1) / 2;
        }

        static TimeSpan Clamp(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
                return TimeSpan.Zero;

            return delay > MaxDelay ? MaxDelay : delay;
        }

        /// <summary>
        /// Google sends <c>Retry-After</c> as seconds or as an HTTP date. A date is resolved
        /// against the response's own <c>Date</c> header rather than this machine's clock, so a
        /// skewed workstation cannot turn "wait 30s" into "wait an hour" or "do not wait at all".
        /// </summary>
        internal static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header is null)
                return null;

            if (header.Delta.HasValue)
                return header.Delta.Value;

            if (!header.Date.HasValue)
                return null;

            var reference = response.Headers.Date ?? header.Date.Value;
            return header.Date.Value - reference;
        }

        /// <summary>
        /// Buffers the body before reading it: the caller reads the same content again to build
        /// the exception it throws when we decline to retry.
        /// </summary>
        internal static async Task<bool> IsRateLimitedAsync(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return true;

            // Drive still answers a burst with 403 and the reason in the body. 403 is also how
            // permission errors and an exhausted *daily* quota arrive, and neither is worth
            // waiting eight seconds for, so the reason decides.
            if (response.StatusCode != HttpStatusCode.Forbidden)
                return false;

            await response.Content.LoadIntoBufferAsync();
            var body = await response.Content.ReadAsStringAsync();

            return HasRetryableReason(body);
        }

        static bool HasRetryableReason(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return false;

            try
            {
                using var document = JsonDocument.Parse(body);

                if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                    return false;

                if (error.TryGetProperty("status", out var status) && IsRetryableStatus(status.GetString()))
                    return true;

                if (!error.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (var single in errors.EnumerateArray())
                {
                    if (single.ValueKind == JsonValueKind.Object
                        && single.TryGetProperty("reason", out var reason)
                        && IsRetryableReason(reason.GetString()))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (JsonException)
            {
                // An HTML error page from a proxy. Nothing says this was about rate.
                return false;
            }
        }

        static bool IsRetryableStatus(string? status) =>
            string.Equals(status, "RESOURCE_EXHAUSTED", StringComparison.Ordinal);

        /// <summary>
        /// Per-minute and per-user limits refill on their own; <c>dailyLimitExceeded</c> and
        /// <c>quotaExceeded</c> do not refill inside the life of a command, so they are failures
        /// rather than delays.
        /// </summary>
        static bool IsRetryableReason(string? reason) =>
            string.Equals(reason, "rateLimitExceeded", StringComparison.Ordinal)
            || string.Equals(reason, "userRateLimitExceeded", StringComparison.Ordinal);
    }

    /// <summary>
    /// Recognises the failure the retries above gave up on, so the shell can say "wait and try
    /// again" instead of printing Google's stack of JSON at someone.
    /// </summary>
    public static class GoogleRateLimit
    {
        public static bool IsRateLimited(Exception? exception)
        {
            switch (exception)
            {
                case null:
                    return false;

                case AggregateException aggregate:
                    // An upload reports its failure wrapped.
                    return aggregate.InnerExceptions.Any(IsRateLimited);

                case GoogleApiException google:
                    return IsRateLimited(google);

                default:
                    return IsRateLimited(exception.InnerException);
            }
        }

        static bool IsRateLimited(GoogleApiException exception)
        {
            if (exception.HttpStatusCode == HttpStatusCode.TooManyRequests)
                return true;

            if (exception.HttpStatusCode != HttpStatusCode.Forbidden || exception.Error?.Errors is null)
                return false;

            return exception.Error.Errors.Any(single =>
                string.Equals(single.Reason, "rateLimitExceeded", StringComparison.Ordinal)
                || string.Equals(single.Reason, "userRateLimitExceeded", StringComparison.Ordinal));
        }
    }
}
