using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Noogen.Providers.GoogleWorkspace.Security;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    /// <summary>
    /// One HTTP exchange, captured. The gateways are thin translators from our vocabulary into
    /// Google's REST shapes, so what they put on the wire is the behaviour worth asserting —
    /// query strings, render options, and the batch requests they build.
    /// </summary>
    public class RecordedRequest
    {
        public HttpMethod Method { get; set; } = HttpMethod.Get;

        public Uri Uri { get; set; } = new("https://example.invalid/");

        public string Body { get; set; } = string.Empty;

        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Path => Uri.AbsolutePath;

        /// <summary>A decoded query-string parameter, or null when it was not sent at all.</summary>
        public string? Parameter(string name)
        {
            foreach (var pair in Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];

                if (!string.Equals(key, name, StringComparison.Ordinal))
                    continue;

                return separator < 0
                    ? string.Empty
                    : Uri.UnescapeDataString(pair[(separator + 1)..].Replace("+", " ", StringComparison.Ordinal));
            }

            return null;
        }

        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

        /// <summary>The request body as JSON. Cloned, so it outlives the document that parsed it.</summary>
        public JsonElement Json()
        {
            using var document = JsonDocument.Parse(Body);
            return document.RootElement.Clone();
        }
    }

    public class StubResponse
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = "{}";

        public string ContentType { get; set; } = "application/json";

        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static StubResponse Json(string body) => new() { Body = body };

        public static StubResponse Text(string body) => new() { Body = body, ContentType = "text/plain" };

        public static StubResponse Status(HttpStatusCode statusCode) => new() { StatusCode = statusCode, Body = "{}" };

        public StubResponse WithHeader(string name, string value)
        {
            Headers[name] = value;
            return this;
        }
    }

    /// <summary>
    /// Stands in for Google's network. Records every request and replays canned responses in
    /// order, repeating the last one so a test only has to describe the exchanges it cares about.
    /// </summary>
    public class StubHttpHandler : HttpMessageHandler
    {
        readonly IList<StubResponse> _responses;
        int _next;

        public StubHttpHandler(params StubResponse[] responses)
        {
            _responses = responses.Length > 0 ? responses : [StubResponse.Json("{}")];
        }

        public IList<RecordedRequest> Requests { get; } = [];

        public RecordedRequest LastRequest => Requests[^1];

        public static StubHttpHandler Returning(string json) => new(StubResponse.Json(json));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest
            {
                Method = request.Method,
                Uri = request.RequestUri ?? new Uri("https://example.invalid/"),
                Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)
            };

            foreach (var header in request.Headers)
                recorded.Headers[header.Key] = string.Join(",", header.Value);

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    recorded.Headers[header.Key] = string.Join(",", header.Value);
            }

            Requests.Add(recorded);

            var stub = _responses[Math.Min(_next, _responses.Count - 1)];
            _next++;

            var response = new HttpResponseMessage(stub.StatusCode)
            {
                Content = new StringContent(stub.Body, Encoding.UTF8, stub.ContentType),
                RequestMessage = request
            };

            foreach (var header in stub.Headers)
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return response;
        }
    }

    /// <summary>
    /// Hands the Google client library our stub handler instead of a real one. This is the
    /// supported extension point, so the rest of the pipeline — retries, headers, serialisation —
    /// stays exactly as it is in production.
    /// </summary>
    public class StubHttpClientFactory : HttpClientFactory
    {
        readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => _handler;
    }

    public static class StubGoogle
    {
        public static BaseClientService.Initializer Initializer(HttpMessageHandler handler) => new()
        {
            HttpClientFactory = new StubHttpClientFactory(handler),
            ApplicationName = "Noogen.Backlog.Tests",
            GZipEnabled = false
        };

        public static DriveService DriveService(HttpMessageHandler handler) => new(Initializer(handler));

        public static SheetsService SheetsService(HttpMessageHandler handler) => new(Initializer(handler));
    }

    public class StubDriveClientFactory : IDriveClientFactory
    {
        readonly DriveService _service;

        public StubDriveClientFactory(HttpMessageHandler handler)
        {
            _service = StubGoogle.DriveService(handler);
        }

        public DriveService Create() => _service;
    }

    public class StubSheetsClientFactory : ISheetsClientFactory
    {
        readonly SheetsService _service;

        public StubSheetsClientFactory(HttpMessageHandler handler)
        {
            _service = StubGoogle.SheetsService(handler);
        }

        public SheetsService Create() => _service;
    }

    /// <summary>
    /// Records what the retry would have waited instead of waiting it, and returns a fixed
    /// jitter fraction so a backoff test asserts one number rather than a range.
    /// </summary>
    public class RecordingRetryScheduler : IRetryScheduler
    {
        public IList<TimeSpan> Waits { get; } = [];

        /// <summary>Zero puts every delay at the bottom of its jitter band.</summary>
        public double Fraction { get; set; }

        public double NextFraction() => Fraction;

        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Waits.Add(duration);
            return Task.CompletedTask;
        }
    }

    public class RecordingRetryListener : IRetryListener
    {
        public IList<int> Attempts { get; } = [];

        public IList<int> MaxAttempts { get; } = [];

        public IList<TimeSpan> Delays { get; } = [];

        public void RateLimited(int attempt, int maxAttempts, TimeSpan delay)
        {
            Attempts.Add(attempt);
            MaxAttempts.Add(maxAttempts);
            Delays.Add(delay);
        }
    }

    /// <summary>A credential that signs nothing, for tests that only care about plumbing.</summary>
    public class StubCredential : IConfigurableHttpClientInitializer
    {
        public int InitializeCount { get; private set; }

        public void Initialize(ConfigurableHttpClient httpClient) => InitializeCount++;
    }

    /// <summary>
    /// A keystore that lives in the test process. Deterministic and reversible, so a test can
    /// assert what reached disk without depending on whatever keystore the CI box happens to have.
    /// </summary>
    public class ReversingTokenProtector : ITokenProtector
    {
        public string Description => "test protector";

        public bool IsOsBacked => true;

        public IList<string> Removed { get; } = [];

        public string Protect(string key, string plaintext) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext).Reverse().ToArray());

        public string? Unprotect(string key, string ciphertext)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext).Reverse().ToArray());
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public void Remove(string key) => Removed.Add(key);
    }

    /// <summary>A temporary directory that cleans up after itself.</summary>
    public class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
