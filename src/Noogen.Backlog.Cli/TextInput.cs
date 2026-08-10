using System.Text;

namespace Noogen.Backlog.Cli
{
    /// <summary>
    /// Prose that must not go through command-line argument parsing.
    ///
    /// Windows hands a native process one command-line string, which .NET splits by
    /// <c>CommandLineToArgvW</c> rules. PowerShell quotes an argument containing whitespace but
    /// does not escape a double quote already inside it, so the embedded quote closes the quoted
    /// run early and the rest of the value is re-tokenised into separate arguments.
    /// <see cref="Verbs"/> refuses those now instead of dropping them — but refusing is only half
    /// an answer, because the description still has to get in. A file path or a pipe is a single
    /// argument whatever it contains, so neither can be split.
    /// </summary>
    public static class TextInput
    {
        /// <summary>The conventional "read it from stdin" spelling, accepted by both flags.</summary>
        public const string StandardInput = "-";

        /// <summary>
        /// The description, from whichever of the three ways it was given, or null for "not given"
        /// — which on <c>edit</c> means "leave the body alone".
        ///
        /// <c>--description</c> is the convenient one and the one the shell can damage. The other
        /// two name a file or a pipe, and neither can be split by quoting: a path is one argument
        /// whatever is inside the file it points at.
        /// </summary>
        public static string? ReadDescription(CommandLine command)
        {
            var inline = command.Has("description");
            var file = command.Has("description-file");

            if (inline && file)
                throw new UsageException("Pass either --description or --description-file, not both.");

            if (file)
                return Read(command.RequireOption("description-file"), "--description-file");

            if (!inline)
                return null;

            // No guard against a bare `--description` here any more: Verbs declares it as an
            // option that takes a value, so the parser refuses one without and this is only ever
            // reached with the value in hand.
            var value = command.RequireOption("description");

            return value == StandardInput ? Read(value, "--description") : value;
        }

        /// <summary>
        /// Reads <paramref name="source"/> — a file path, or <see cref="StandardInput"/>.
        /// <paramref name="flag"/> is the option that named it, so an error can quote what was
        /// actually typed.
        /// </summary>
        public static string Read(string source, string flag)
        {
            var text = source == StandardInput ? ReadStandardInput(flag) : ReadFile(source, flag);

            // A path that exists but is empty, or a pipe that carried nothing, is a mistake worth
            // a stop: this whole flag exists so that prose which does not arrive fails loudly
            // rather than landing as a truncated ticket.
            if (string.IsNullOrWhiteSpace(text))
                throw new UsageException($"{flag} {source} came back empty, so there is nothing to write.");

            return text;
        }

        static string ReadFile(string path, string flag)
        {
            if (!File.Exists(path))
            {
                var hint = Directory.Exists(path) ? " That is a directory." : string.Empty;
                throw new UsageException($"{flag}: no such file '{path}'.{hint}");
            }

            return Decode(File.ReadAllBytes(path));
        }

        static string ReadStandardInput(string flag)
        {
            // Without this the process would sit on a terminal's stdin waiting for a Ctrl-Z that
            // the person does not know it wants, which reads as a hang.
            if (!Console.IsInputRedirected)
            {
                throw new UsageException(
                    $"{flag} - reads the description from standard input, but nothing is piped in. "
                    + "Pipe it (Get-Content body.md -Raw | backlog ...) or use --description-file instead.");
            }

            using var stream = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            return Decode(buffer.ToArray());
        }

        /// <summary>
        /// Bytes to text: a BOM if there is one, then UTF-8, then the console's own encoding.
        ///
        /// The order matters and the fallback is the point. A file anyone writes today is UTF-8,
        /// so that is the default — but PowerShell 5.1 encodes what it pipes to a native process
        /// using <c>[Console]::OutputEncoding</c>, which on Windows is the OEM code page. Decoding
        /// those bytes as UTF-8 turns every em dash and curly quote into U+FFFD, which is the same
        /// class of silent corruption this input path exists to end. So UTF-8 is tried
        /// <em>strictly</em> — an invalid sequence throws rather than substituting — and only a
        /// genuine failure falls back to the code page the bytes most likely came from.
        ///
        /// Well-formed ASCII decodes identically either way, so the fallback only ever runs on
        /// input that UTF-8 could not have produced.
        /// </summary>
        public static string Decode(byte[] bytes) => Decode(bytes, Console.InputEncoding);

        /// <summary>The seam for <see cref="Decode(byte[])"/>: a test cannot set a console encoding.</summary>
        public static string Decode(byte[] bytes, Encoding fallback)
        {
            if (bytes.Length == 0)
                return string.Empty;

            var byName = FromByteOrderMark(bytes);
            if (byName is not null)
                return byName;

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return fallback.GetString(bytes);
            }
        }

        /// <summary>
        /// The encodings a Windows tool actually emits when it marks one. A BOM is an explicit
        /// statement about the bytes, so it outranks both guesses.
        /// </summary>
        static string? FromByteOrderMark(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

            // Before UTF-16 LE, whose BOM is this one's first two bytes.
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return new UTF32Encoding(false, false).GetString(bytes, 4, bytes.Length - 4);

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            return null;
        }
    }
}
