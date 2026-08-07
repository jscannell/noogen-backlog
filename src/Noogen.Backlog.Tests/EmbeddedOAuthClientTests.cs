using Noogen.Backlog.Cli;
using Noogen.Providers.GoogleWorkspace;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// The build bakes a gitignored oauth.json into the CLI so a distributed install works with
    /// nothing on disk. The rest of OAuth client resolution is covered in the provider's own test
    /// project; this is the half that can only be asserted against the assembly that carries it.
    /// </summary>
    public class EmbeddedOAuthClientTests : IDisposable
    {
        readonly string _directory = Path.Combine(Path.GetTempPath(), "noogen-embedded-oauth-" + Guid.NewGuid().ToString("N"));
        readonly string? _originalClientId;
        readonly string? _originalClientSecret;

        public EmbeddedOAuthClientTests()
        {
            // A developer running the suite may have the real client in their environment, and it
            // would otherwise win over everything these tests set up.
            _originalClientId = Environment.GetEnvironmentVariable(OAuthClientSettings.ClientIdEnvironmentVariable);
            _originalClientSecret = Environment.GetEnvironmentVariable(OAuthClientSettings.ClientSecretEnvironmentVariable);

            SetEnvironment(null, null);
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            SetEnvironment(_originalClientId, _originalClientSecret);

            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [Fact]
        public void Resolve_NoFileOnDisk_FallsBackToWhateverTheBuildEmbedded()
        {
            // Whether the CLI assembly carries one depends on whether the build had the gitignored
            // oauth.json, so assert the behaviour for whichever case this build is — both are
            // legitimate, and a contributor without the secret must still get a working build.
            var cli = typeof(Program).Assembly;
            var hasEmbedded = cli.GetManifestResourceNames()
                .Any(name => name.EndsWith(OAuthClientSettings.EmbeddedResourceName, StringComparison.OrdinalIgnoreCase));

            var settings = OAuthClientSettings.Resolve(Path.Combine(_directory, "absent.json"), cli);

            if (hasEmbedded)
            {
                Assert.True(settings.IsConfigured);
                Assert.Equal("built into this tool", settings.Source);
            }
            else
            {
                Assert.False(settings.IsConfigured);
            }
        }

        [Fact]
        public void Resolve_LocalFileAndEmbeddedClientBothPresent_PrefersTheLocalFile()
        {
            // An override should beat a default, so someone can point at a different client
            // without rebuilding the tool.
            var path = Path.Combine(_directory, "oauth.json");
            File.WriteAllText(path, """{ "clientId": "override", "clientSecret": "override-secret" }""");

            var settings = OAuthClientSettings.Resolve(path, typeof(Program).Assembly);

            Assert.Equal("override", settings.ClientId);
            Assert.Equal(path, settings.Source);
        }

        static void SetEnvironment(string? clientId, string? clientSecret)
        {
            Environment.SetEnvironmentVariable(OAuthClientSettings.ClientIdEnvironmentVariable, clientId);
            Environment.SetEnvironmentVariable(OAuthClientSettings.ClientSecretEnvironmentVariable, clientSecret);
        }
    }
}
