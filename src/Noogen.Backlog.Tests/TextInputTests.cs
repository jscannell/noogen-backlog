using System.Text;
using Noogen.Backlog.Cli;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// NG-0045: a description containing a double quote was torn apart by the shell and silently
    /// truncated. <see cref="Verbs"/> makes the wreckage loud; these are the two input paths that
    /// avoid producing it, because a path and a pipe are each one argument no matter what is
    /// inside them.
    /// </summary>
    public class TextInputTests : IDisposable
    {
        readonly string _directory =
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "noogen-textinput-" + Guid.NewGuid().ToString("n"))).FullName;

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        string Write(string name, string content) => Write(name, Encoding.UTF8.GetBytes(content));

        static string? Describe(params string[] args) => TextInput.ReadDescription(CommandLine.Parse(args));

        // --- the file path ---

        [Fact]
        public void ReadDescription_FromAFile_ReturnsEveryCharacterIncludingQuotes()
        {
            var body = "L1 plain ascii only END1\n\nL2 has \"double quoted\" words END2\n\nL3 final line END3";
            var path = Write("body.md", body);

            Assert.Equal(body, Describe("new", "--title", "probe", "--description-file", path));
        }

        [Fact]
        public void ReadDescription_FileThatDoesNotExist_Refuses()
        {
            var path = Path.Combine(_directory, "absent.md");

            var exception = Assert.Throws<UsageException>(() => Describe("new", "--description-file", path));

            Assert.Contains("no such file", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ReadDescription_FileThatIsADirectory_SaysSo()
        {
            var exception = Assert.Throws<UsageException>(() => Describe("new", "--description-file", _directory));

            Assert.Contains("directory", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>The failure this whole flag exists to prevent is prose that quietly went missing.</summary>
        [Fact]
        public void ReadDescription_EmptyFile_RefusesRatherThanWritingNothing()
        {
            var path = Write("empty.md", "   \n\n  ");

            var exception = Assert.Throws<UsageException>(() => Describe("edit", "NG-12", "--description-file", path));

            Assert.Contains("empty", exception.Message, StringComparison.Ordinal);
        }

        // --- the three ways, and what they mean together ---

        [Fact]
        public void ReadDescription_NeitherFlag_IsNullSoTheBodyIsLeftAlone() =>
            Assert.Null(Describe("edit", "NG-12", "--title", "Renamed"));

        [Fact]
        public void ReadDescription_InlineValue_IsUsedVerbatim() =>
            Assert.Equal("short one", Describe("edit", "NG-12", "--description", "short one"));

        [Fact]
        public void ReadDescription_BothFlags_Refuses()
        {
            var path = Write("body.md", "from the file");

            var exception = Assert.Throws<UsageException>(
                () => Describe("new", "--description", "inline", "--description-file", path));

            Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A bare `--description` is refused by the parser now (NG-0058) rather than by a special
        /// case here — but reading it as "no change" is the silent no-op either way, so the
        /// guarantee is pinned from this side too.
        /// </summary>
        [Fact]
        public void ReadDescription_FlagWithNoValue_Refuses() =>
            Assert.Throws<UsageException>(() => Describe("edit", "NG-12", "--description"));

        [Fact]
        public void ReadDescription_StdinSentinelWithNothingPiped_RefusesInsteadOfWaiting()
        {
            // Console.IsInputRedirected is a property of the test host, not something a test can
            // set; under `dotnet test` stdin is redirected, so this asserts the reachable half —
            // the read happens and finds nothing, rather than the value "-" landing as prose.
            var exception = Assert.Throws<UsageException>(() => Describe("new", "--description", "-"));

            Assert.DoesNotContain("landed", exception.Message, StringComparison.Ordinal);
            Assert.True(
                exception.Message.Contains("standard input", StringComparison.Ordinal)
                    || exception.Message.Contains("empty", StringComparison.Ordinal),
                exception.Message);
        }

        // --- decoding ---
        //
        // PowerShell 5.1 encodes what it pipes to a native process with [Console]::OutputEncoding,
        // which on Windows is the OEM code page — so bytes arriving here are not reliably UTF-8,
        // and decoding them as if they were would substitute U+FFFD for every em dash. That is the
        // same silent corruption in a new place.

        [Fact]
        public void Decode_Utf8WithoutABom_IsReadAsUtf8() =>
            Assert.Equal("em — dash", TextInput.Decode(Encoding.UTF8.GetBytes("em — dash"), Encoding.Latin1));

        // Latin1 stands in for whatever single-byte code page the console is on — it is the one
        // .NET carries without the CodePages provider, and it fails as UTF-8 the same way 1252
        // does. `é` is 0xE9, which starts a three-byte UTF-8 sequence that is not there.

        [Fact]
        public void Decode_BytesThatAreNotValidUtf8_FallsBackToTheConsoleEncoding()
        {
            var bytes = Encoding.Latin1.GetBytes("café naïve");

            Assert.Equal("café naïve", TextInput.Decode(bytes, Encoding.Latin1));
        }

        [Fact]
        public void Decode_BytesThatAreNotValidUtf8_NeverSubstitutesReplacementCharacters()
        {
            var bytes = Encoding.Latin1.GetBytes("café naïve");

            Assert.DoesNotContain('�', TextInput.Decode(bytes, Encoding.Latin1));
        }

        [Theory]
        [InlineData("utf-8")]
        [InlineData("utf-16")]
        [InlineData("utf-16BE")]
        [InlineData("utf-32")]
        public void Decode_ABomOutranksBothGuesses(string name)
        {
            var encoding = Encoding.GetEncoding(name);
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("em — dash")).ToArray();

            Assert.Equal("em — dash", TextInput.Decode(bytes, Encoding.Latin1));
        }

        [Fact]
        public void Decode_NoBytes_IsEmpty() =>
            Assert.Equal(string.Empty, TextInput.Decode([], Encoding.UTF8));

        [Fact]
        public void ReadDescription_FileWrittenAsUtf16_RoundTrips()
        {
            var path = Write("utf16.md", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("a “quoted” line")).ToArray());

            Assert.Equal("a “quoted” line", Describe("new", "--description-file", path));
        }
    }
}
