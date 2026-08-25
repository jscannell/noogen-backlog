using System.Text.Json.Nodes;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Mcp
{
    /// <summary>
    /// What one call said, checked against what its verb reads.
    ///
    /// This is the MCP counterpart of the command line's own validation, and it is doing the same
    /// job for the same reason: an option nobody declared used to be a silent no-op — the command
    /// ran, did nothing, and reported success. Here it is also the whole of the disclosure. The
    /// tool arrives carrying a verb list and nothing else, so a caller learns the options by asking
    /// for help or by getting one wrong, which means a refusal has to be worth reading: it names
    /// the verb, what it accepts, and where to find out what each one is for.
    ///
    /// Everything a verb reads arrives under one object, the positional argument included. There
    /// is no argument position over MCP and no shell to damage a value, so the command line's
    /// <c>--name-file</c> and <c>--name -</c> spellings are neither offered nor needed — prose is
    /// an ordinary JSON string, newlines and quotes and all.
    /// </summary>
    public class VerbArguments
    {
        readonly VerbDefinition _verb;
        readonly JsonObject _values;

        VerbArguments(VerbDefinition verb, JsonObject values)
        {
            _verb = verb;
            _values = values;
        }

        /// <summary>
        /// Reads <paramref name="options"/> for <paramref name="verb"/>, refusing anything it does
        /// not declare before the call costs a request.
        /// </summary>
        public static VerbArguments Read(VerbDefinition verb, JsonObject? options)
        {
            var values = new JsonObject();

            foreach (var pair in options ?? new JsonObject())
            {
                var name = Resolve(verb, pair.Key)
                    ?? throw new UsageException(Unaccepted(verb, pair.Key));

                // A JSON null is how a client spells "I am not passing this". Keeping it would make
                // `{"area": null}` mean something different from leaving `area` out, which is not a
                // distinction any verb here draws.
                if (pair.Value is null)
                    continue;

                // Both spellings of a score at once. An object cannot hold the same key twice, so
                // nothing below here could tell there had been two — the second would simply
                // overwrite the first and one of the caller's numbers would vanish without a word.
                if (values.ContainsKey(name))
                {
                    throw new UsageException(
                        $"'{verb.Name}' was given '{name}' twice, under two spellings. They are the "
                        + "same option, so one of the two values would be lost — pass whichever one is meant.");
                }

                values[name] = pair.Value.DeepClone();
            }

            return new VerbArguments(verb, values);
        }

        /// <summary>
        /// The declared name behind a spelling, or null if the verb does not read it. The
        /// positional counts as one: over MCP it arrives under its own name like everything else.
        /// </summary>
        static string? Resolve(VerbDefinition verb, string name)
        {
            if (verb.Positional is not null && Same(verb.PositionalName, name))
                return verb.PositionalName;

            foreach (var option in verb.OptionsOn(VerbSurface.Mcp))
            {
                if (Same(option.Name, name) || (option.Alias is not null && Same(option.Alias, name)))
                    return option.Name;
            }

            return null;
        }

        static bool Same(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Names the option, then what to do instead. The generic half lists what the verb reads
        /// and where to read about it; <see cref="VerbCatalog.GuidanceFor"/> replaces it where a
        /// wrong guess has a known right answer, because "it accepts: ..." does not answer "then
        /// how do I change the status?" — and that question is why the option was reached for.
        /// </summary>
        static string Unaccepted(VerbDefinition verb, string name)
        {
            var detail = VerbCatalog.GuidanceFor(verb.Name, name)
                ?? $"'{verb.Name}' reads: {string.Join(", ", Accepted(verb))}. "
                   + $"Call {{\"verb\": \"help\", \"options\": {{\"verb\": \"{verb.Name}\"}}}} for what each one is for.";

            return $"'{verb.Name}' does not accept '{name}'. {detail}";
        }

        /// <summary>Every name this verb reads over MCP, in the order its help lists them.</summary>
        public static IReadOnlyList<string> Accepted(VerbDefinition verb)
        {
            var names = new List<string>();

            if (verb.Positional is not null)
                names.Add(verb.PositionalName);

            foreach (var option in verb.OptionsOn(VerbSurface.Mcp))
                names.Add(option.Name);

            return names;
        }

        public bool Has(string name) => _values.ContainsKey(name);

        /// <summary>
        /// A value as text. Numbers and booleans come through as what they were written as, so a
        /// caller that sends <c>{"bv": 8}</c> and one that sends <c>{"bv": "8"}</c> are both
        /// understood; an object or an array is a mistake worth naming.
        /// </summary>
        public string? Text(string name)
        {
            if (!_values.TryGetPropertyValue(name, out var node) || node is null)
                return null;

            if (node is not JsonValue value)
            {
                throw new UsageException(
                    $"'{_verb.Name}' expects '{name}' to be a single value, and got "
                    + (node is JsonArray ? "an array." : "an object."));
            }

            return value.ToString();
        }

        public string Require(string name)
        {
            var text = Text(name);

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new UsageException(
                    $"'{_verb.Name}' needs '{name}'. Call {{\"verb\": \"help\", \"options\": "
                    + $"{{\"verb\": \"{_verb.Name}\"}}}} for what it is.");
            }

            return text;
        }

        /// <summary>The ticket id, or the search text — whatever this verb's one argument is.</summary>
        public string RequireSubject() => Require(_verb.PositionalName);

        /// <summary>
        /// A whole number. Both spellings of a score reach this under the short one — an alias is
        /// resolved to the name the catalog declares on the way in, so nothing below here has to
        /// know there were two.
        /// </summary>
        public int? Number(string name) => OptionValue.WholeNumber(Text(name), $"'{name}'");

        /// <summary>
        /// A valueless flag. It carries a boolean here because JSON has one — the command line's
        /// "present or absent" is a property of a command line, not of the option.
        /// </summary>
        public bool Flag(string name)
        {
            if (!_values.TryGetPropertyValue(name, out var node) || node is null)
                return false;

            if (node is JsonValue value && value.TryGetValue<bool>(out var flag))
                return flag;

            var text = node is JsonValue scalar ? scalar.ToString() : null;

            if (bool.TryParse(text, out var parsed))
                return parsed;

            throw new UsageException($"'{name}' is either true or false, and got '{text ?? "a structure"}'.");
        }

        /// <summary>
        /// Free text a person wrote, or null when the option was not given — which on <c>edit</c>
        /// means "leave that section alone".
        ///
        /// An empty string is refused rather than passed on. Blank is how a caller would spell
        /// "clear it", and the two sections this can reach are the one thing here with no other
        /// copy: the Sheet holds every scalar field, but nothing holds a description except the
        /// document. Refusing costs a retry; accepting costs somebody's writing.
        /// </summary>
        public string? Prose(string name)
        {
            var text = Text(name);

            if (text is null)
                return null;

            if (string.IsNullOrWhiteSpace(text))
                throw new UsageException($"'{name}' was given as empty text, so there is nothing to write.");

            return text;
        }

        public string RequireProse(string name) =>
            Prose(name) ?? throw new UsageException(
                $"'{_verb.Name}' needs '{name}' — the words to record. Pass it as an ordinary string; "
                + "newlines and quotes survive intact.");
    }
}
