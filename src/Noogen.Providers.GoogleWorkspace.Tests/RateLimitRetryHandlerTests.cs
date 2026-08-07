using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Google;
using Google.Apis.Http;
using Google.Apis.Requests;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    public class RateLimitRetryHandlerTests
    {
        [Fact]
        public async Task HandleResponseAsync_TooManyRequests_WaitsAndRetries()
        {
            var scheduler = new RecordingRetryScheduler();
            var handler = new RateLimitRetryHandler(scheduler: scheduler);

            var retry = await handler.HandleResponseAsync(Args(Response(HttpStatusCode.TooManyRequests)));

            Assert.True(retry);
            Assert.Single(scheduler.Waits);
        }

        [Fact]
        public async Task HandleResponseAsync_ForbiddenWithUserRateLimitExceeded_Retries()
        {
            // Drive answers a burst with 403 and the reason in the body rather than with 429.
            var scheduler = new RecordingRetryScheduler();
            var handler = new RateLimitRetryHandler(scheduler: scheduler);

            var response = Response(HttpStatusCode.Forbidden, """
                {"error":{"code":403,"errors":[{"reason":"userRateLimitExceeded","message":"User Rate Limit Exceeded"}]}}
                """);

            Assert.True(await handler.HandleResponseAsync(Args(response)));
        }

        [Fact]
        public async Task HandleResponseAsync_ForbiddenWithResourceExhaustedStatus_Retries()
        {
            var handler = new RateLimitRetryHandler(scheduler: new RecordingRetryScheduler());

            var response = Response(HttpStatusCode.Forbidden, """
                {"error":{"code":403,"status":"RESOURCE_EXHAUSTED","message":"Quota exceeded"}}
                """);

            Assert.True(await handler.HandleResponseAsync(Args(response)));
        }

        [Fact]
        public async Task HandleResponseAsync_ForbiddenWithDailyQuotaExhausted_DoesNotRetry()
        {
            // A daily quota does not refill inside the life of a command, so waiting eight
            // seconds only delays the same failure.
            var scheduler = new RecordingRetryScheduler();
            var handler = new RateLimitRetryHandler(scheduler: scheduler);

            var response = Response(HttpStatusCode.Forbidden, """
                {"error":{"code":403,"errors":[{"reason":"dailyLimitExceeded","message":"Daily Limit Exceeded"}]}}
                """);

            Assert.False(await handler.HandleResponseAsync(Args(response)));
            Assert.Empty(scheduler.Waits);
        }

        [Fact]
        public async Task HandleResponseAsync_ForbiddenForPermissions_DoesNotRetry()
        {
            var handler = new RateLimitRetryHandler(scheduler: new RecordingRetryScheduler());

            var response = Response(HttpStatusCode.Forbidden, """
                {"error":{"code":403,"errors":[{"reason":"insufficientFilePermissions"}]}}
                """);

            Assert.False(await handler.HandleResponseAsync(Args(response)));
        }

        [Fact]
        public async Task HandleResponseAsync_ForbiddenWithAnHtmlBody_DoesNotRetry()
        {
            // A proxy's error page is not JSON, and nothing in it says this was about rate.
            var handler = new RateLimitRetryHandler(scheduler: new RecordingRetryScheduler());

            var response = Response(HttpStatusCode.Forbidden, "<html><body>Forbidden</body></html>");

            Assert.False(await handler.HandleResponseAsync(Args(response)));
        }

        [Fact]
        public async Task HandleResponseAsync_ServerError_DoesNotRetry()
        {
            // 503 and transport faults are the library's own backoff policy; quota is the gap
            // this handler fills, and handling both would double the waiting.
            var handler = new RateLimitRetryHandler(scheduler: new RecordingRetryScheduler());

            Assert.False(await handler.HandleResponseAsync(Args(Response(HttpStatusCode.ServiceUnavailable))));
        }

        [Fact]
        public async Task HandleResponseAsync_MessageHandlerOutOfTries_DoesNotWaitForARetryThatCannotHappen()
        {
            // A service built with a smaller budget than this handler would take: returning true
            // there buys a wait and then the same failure.
            var scheduler = new RecordingRetryScheduler();
            var handler = new RateLimitRetryHandler(scheduler: scheduler, maxAttempts: 5);

            var args = Args(Response(HttpStatusCode.TooManyRequests));
            args.TotalTries = 2;
            args.CurrentFailedTry = 2;

            Assert.False(await handler.HandleResponseAsync(args));
            Assert.Empty(scheduler.Waits);
        }

        [Fact]
        public async Task HandleResponseAsync_LastAllowedAttempt_GivesUpRatherThanWaitingForNothing()
        {
            var scheduler = new RecordingRetryScheduler();
            var handler = new RateLimitRetryHandler(scheduler: scheduler, maxAttempts: 3);

            var args = Args(Response(HttpStatusCode.TooManyRequests));
            args.CurrentFailedTry = 3;

            Assert.False(await handler.HandleResponseAsync(args));
            Assert.Empty(scheduler.Waits);
        }

        [Fact]
        public async Task HandleResponseAsync_RetryAfterInSeconds_WaitsExactlyThatLong()
        {
            var scheduler = new RecordingRetryScheduler();
            var handler = new RateLimitRetryHandler(scheduler: scheduler);

            var response = Response(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));

            Assert.True(await handler.HandleResponseAsync(Args(response)));
            Assert.Equal(TimeSpan.FromSeconds(12), Assert.Single(scheduler.Waits));
        }

        [Fact]
        public async Task HandleResponseAsync_RetryAfterAsADate_MeasuresItAgainstTheServersOwnClock()
        {
            // Resolving the date against this machine's clock would let a skewed workstation turn
            // "wait 30s" into "wait an hour" or into no wait at all.
            var scheduler = new RecordingRetryScheduler();
            var handler = new RateLimitRetryHandler(scheduler: scheduler);

            var serverNow = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

            var response = Response(HttpStatusCode.TooManyRequests);
            response.Headers.Date = serverNow;
            response.Headers.RetryAfter = new RetryConditionHeaderValue(serverNow.AddSeconds(30));

            Assert.True(await handler.HandleResponseAsync(Args(response)));
            Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(scheduler.Waits));
        }

        [Fact]
        public async Task HandleResponseAsync_Retrying_TellsTheListenerHowLongItIsAboutToBeQuiet()
        {
            var listener = new RecordingRetryListener();
            var scheduler = new RecordingRetryScheduler();
            var handler = new RateLimitRetryHandler(listener, scheduler, maxAttempts: 4);

            var response = Response(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));

            await handler.HandleResponseAsync(Args(response));

            Assert.Equal(1, Assert.Single(listener.Attempts));
            Assert.Equal(4, Assert.Single(listener.MaxAttempts));
            Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(listener.Delays));
        }

        [Fact]
        public async Task HandleResponseAsync_DeclinedToRetry_LeavesTheBodyReadableForTheCaller()
        {
            // The caller reads the same content again to build the exception it throws, so
            // inspecting the reason must not consume it.
            var handler = new RateLimitRetryHandler(scheduler: new RecordingRetryScheduler());

            var body = """{"error":{"code":403,"errors":[{"reason":"dailyLimitExceeded"}]}}""";
            var response = Response(HttpStatusCode.Forbidden, body);

            await handler.HandleResponseAsync(Args(response));

            Assert.Equal(body, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public void Delay_ConsecutiveFailures_DoublesTheWait()
        {
            Assert.Equal(TimeSpan.FromSeconds(0.5), RateLimitRetryHandler.Delay(1, null, 0));
            Assert.Equal(TimeSpan.FromSeconds(1), RateLimitRetryHandler.Delay(2, null, 0));
            Assert.Equal(TimeSpan.FromSeconds(2), RateLimitRetryHandler.Delay(3, null, 0));
            Assert.Equal(TimeSpan.FromSeconds(4), RateLimitRetryHandler.Delay(4, null, 0));
        }

        [Fact]
        public void Delay_FullJitter_NeverExceedsTheUnjitteredBackoff()
        {
            // Equal jitter: half the wait is fixed, half is spread. Full jitter could return
            // nearly nothing, which against a per-minute quota is just another rejection.
            Assert.Equal(TimeSpan.FromSeconds(1), RateLimitRetryHandler.Delay(1, null, 1));
            Assert.Equal(TimeSpan.FromSeconds(0.75), RateLimitRetryHandler.Delay(1, null, 0.5));
        }

        [Fact]
        public void Delay_AbsurdRetryAfter_IsCappedRatherThanObeyed()
        {
            Assert.Equal(RateLimitRetryHandler.MaxDelay, RateLimitRetryHandler.Delay(1, TimeSpan.FromHours(1), 0));
        }

        [Fact]
        public void Delay_RetryAfterAlreadyPassed_DoesNotGoNegative()
        {
            Assert.Equal(TimeSpan.Zero, RateLimitRetryHandler.Delay(1, TimeSpan.FromSeconds(-5), 0));
        }

        [Fact]
        public void Constructor_MoreAttemptsThanTheMessageHandlerAllows_Throws() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitRetryHandler(maxAttempts: 99));

        static HttpResponseMessage Response(HttpStatusCode statusCode, string body = "{}") =>
            new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        static HandleUnsuccessfulResponseArgs Args(HttpResponseMessage response) => new()
        {
            Request = new HttpRequestMessage(HttpMethod.Get, "https://sheets.googleapis.com/v4/spreadsheets/x"),
            Response = response,
            TotalTries = RateLimitRetryHandler.DefaultMaxAttempts,
            CurrentFailedTry = 1
        };
    }

    public class GoogleRateLimitTests
    {
        [Fact]
        public void IsRateLimited_TooManyRequests_IsTrue() =>
            Assert.True(GoogleRateLimit.IsRateLimited(Google(HttpStatusCode.TooManyRequests)));

        [Fact]
        public void IsRateLimited_ForbiddenForRate_IsTrue() =>
            Assert.True(GoogleRateLimit.IsRateLimited(Google(HttpStatusCode.Forbidden, "rateLimitExceeded")));

        [Fact]
        public void IsRateLimited_ForbiddenForPermissions_IsFalse() =>
            Assert.False(GoogleRateLimit.IsRateLimited(Google(HttpStatusCode.Forbidden, "insufficientFilePermissions")));

        [Fact]
        public void IsRateLimited_WrappedByAnUpload_IsStillRecognised()
        {
            // A media upload reports its failure through AggregateException, so the CLI would
            // otherwise print Google's JSON instead of "wait and try again".
            var wrapped = new AggregateException(Google(HttpStatusCode.TooManyRequests));

            Assert.True(GoogleRateLimit.IsRateLimited(wrapped));
        }

        [Fact]
        public void IsRateLimited_NestedAsAnInnerException_IsStillRecognised()
        {
            var wrapped = new InvalidOperationException("could not save", Google(HttpStatusCode.TooManyRequests));

            Assert.True(GoogleRateLimit.IsRateLimited(wrapped));
        }

        [Fact]
        public void IsRateLimited_AnythingElse_IsFalse() =>
            Assert.False(GoogleRateLimit.IsRateLimited(new InvalidOperationException("no")));

        static GoogleApiException Google(HttpStatusCode statusCode, string? reason = null)
        {
            var exception = new GoogleApiException("sheets", "refused") { HttpStatusCode = statusCode };

            if (reason is not null)
            {
                exception.Error = new RequestError
                {
                    Code = (int)statusCode,
                    Errors = [new SingleError { Reason = reason }]
                };
            }

            return exception;
        }
    }
}
