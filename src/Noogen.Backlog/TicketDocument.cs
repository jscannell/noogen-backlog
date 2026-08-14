using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Noogen.Backlog
{
    /// <summary>
    /// The markdown ticket: an `# &lt;ID&gt; — &lt;Title&gt;` heading, a bullet list of the fields a
    /// human edits, then the prose.
    ///
    /// Every part of it renders. Drive is where a person who does not use the CLI reads a ticket,
    /// and Drive does not special-case YAML frontmatter the way a code host does — a `---` block
    /// there shows as literal text or is swallowed as a horizontal rule, so the first thing the
    /// reader saw was machine plumbing.
    ///
    /// The heading is the only home of the id and the title. They used to sit in frontmatter *and*
    /// in a heading that nothing ever rewrote, so `edit --title` left the heading permanently
    /// stale and no check could catch it: <c>doctor</c> compares the Sheet against the metadata,
    /// and those two agreed.
    ///
    /// The Sheet is the source of truth — <c>doctor</c> reports drift and <c>reindex</c> can
    /// rebuild Sheet rows from these documents if the index is ever damaged. Unrecognised keys
    /// round-trip untouched so a field a human adds by hand is not eaten.
    /// </summary>
    public class TicketDocument
    {
        public Ticket Ticket { get; set; } = new();

        /// <summary>
        /// The prose, from the first line that is not part of the metadata block onwards. The store
        /// regenerates the heading and the bullets on every write and copies this through verbatim,
        /// so everything a person types below them is theirs.
        /// </summary>
        public string Body { get; set; } = string.Empty;

        const string Separator = " — ";

        /// <summary>
        /// `- **Key:** value`, also accepting the colon outside the bold and `*` for the bullet,
        /// because a human editing in Drive will type whichever they remember. The key then goes
        /// through <see cref="SheetSchema.Canonical"/>, so 'Job size' and 'job_size' are one field.
        /// </summary>
        static readonly Regex FieldPattern = new(
            @"^\s*[-*]\s+\*\*(?<key>[^*]+?)\*\*\s*:?[ \t]*(?<value>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// What may stand between the id and the title. We write an em dash; a person retyping the
        /// heading may well not. The earliest one in the line wins, so a title that itself contains
        /// a dash or a colon splits after the id rather than inside itself.
        /// </summary>
        static readonly string[] HeadingSeparators = [" — ", " – ", " - ", ": "];

        /// <summary>
        /// A field value Docs turned into a link. Docs autolinks anything email- or URL-shaped when
        /// it imports the document, so `- **Owner:** j@noogen.ai` comes back out of the export as
        /// `- **Owner:** [j@noogen.ai](mailto:j@noogen.ai)`. Left alone that is an owner nobody
        /// typed, and <c>reindex</c> — which takes area and owner from the document — would write
        /// it into the Sheet. `doctor` compares neither, so nothing would have caught it.
        ///
        /// Only a value that is *entirely* one link is unwrapped. These fields are short scalars,
        /// never prose, so a whole-value link is always just its own text wearing a link. Prose is
        /// untouched: a link a person put in the body is theirs and renders as they meant it.
        /// </summary>
        static readonly Regex WholeValueLink = new(
            @"^\[(?<text>[^\]]*)\]\([^()\s]*\)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>The bare `&lt;j@noogen.ai&gt;` autolink, which is the same thing spelled shorter.</summary>
        static readonly Regex WholeValueAutolink = new(
            @"^<(?<text>[^<>\s]+)>$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The ASCII punctuation a backslash may escape in markdown. Docs' export puts one in front
        /// of anything it thinks could be read as markup, so a title with a hyphen or an underscore
        /// — `Fix the sign-in flow`, `follow_up work` — comes back as `Fix the sign\-in flow`.
        /// </summary>
        const string EscapablePunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

        public static TicketDocument Parse(string content)
        {
            ArgumentNullException.ThrowIfNull(content);

            var lines = content.Replace("\r\n", "\n").Split('\n');
            var index = 0;

            while (index < lines.Length && lines[index].Trim().Length == 0)
                index++;

            if (index >= lines.Length)
                throw new FormatException($"Ticket document is empty. It must open with an '# <ID>{Separator}<Title>' heading.");

            var heading = lines[index].Trim();
            if (!heading.StartsWith("# ", StringComparison.Ordinal))
                throw new FormatException($"Ticket document must open with an '# <ID>{Separator}<Title>' heading, not '{heading}'.");

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadHeading(heading, fields);
            index++;

            // The metadata block runs from the heading to the first line that is neither blank nor
            // one of its bullets. Prose typed straight under the heading is body, not a field, and
            // stays exactly where its author put it.
            var bodyStart = lines.Length;

            for (; index < lines.Length; index++)
            {
                if (lines[index].Trim().Length == 0)
                    continue;

                var match = FieldPattern.Match(lines[index]);
                if (!match.Success)
                {
                    bodyStart = index;
                    break;
                }

                var written = Unescape(match.Groups["key"].Value.Trim().TrimEnd(':').Trim());
                var key = SheetSchema.Canonical(written) ?? written;

                // First occurrence wins, as it does in the header row — and the heading was read
                // first, so an `- **ID:**` bullet someone left behind cannot override it.
                if (key.Length > 0 && !fields.ContainsKey(key))
                    fields[key] = Unescape(Unlink(match.Groups["value"].Value.Trim()));
            }

            var body = bodyStart < lines.Length
                ? string.Join('\n', lines[bodyStart..]).Trim('\n')
                : string.Empty;

            return new TicketDocument
            {
                Ticket = ToTicket(fields),
                Body = body
            };
        }

        public static string Serialize(Ticket ticket, string body)
        {
            ArgumentNullException.ThrowIfNull(ticket);

            var builder = new StringBuilder();

            builder.Append("# ").Append(SingleLine(ticket.Id)).Append(Separator).Append(SingleLine(ticket.Title)).Append("\n\n");

            foreach (var field in ToFields(ticket))
                builder.Append("- **").Append(field.Key).Append(":** ").Append(SingleLine(field.Value)).Append('\n');

            builder.Append('\n');
            builder.Append((body ?? string.Empty).Replace("\r\n", "\n").Trim('\n')).Append('\n');

            return builder.ToString();
        }

        public string Serialize() => Serialize(Ticket, Body);

        /// <summary>
        /// The id and the title come from the heading and nowhere else, which is the whole point of
        /// putting them there: one copy cannot drift from another.
        /// </summary>
        static void ReadHeading(string heading, IDictionary<string, string> fields)
        {
            var text = heading[2..].Trim();
            var at = -1;
            var width = 0;

            foreach (var candidate in HeadingSeparators)
            {
                var found = text.IndexOf(candidate, StringComparison.Ordinal);
                if (found > 0 && (at < 0 || found < at))
                {
                    at = found;
                    width = candidate.Length;
                }
            }

            if (at < 0)
                throw new FormatException($"Heading '{heading}' is not '# <ID>{Separator}<Title>'. An em dash separates the id from the title.");

            var title = text[(at + width)..].Trim();
            if (title.Length == 0)
                throw new FormatException($"Heading '{heading}' has an id but no title.");

            // Unescaped after the split, not before: the separator we write is an em dash, which
            // Docs never escapes, so splitting the raw line cannot land inside an escape sequence.
            fields[SheetSchema.Id] = Unescape(text[..at].Trim());
            fields[SheetSchema.Title] = Unescape(title);
        }

        /// <summary>
        /// Gives back the text of a field value that is wholly a link, and anything else unchanged.
        /// See <see cref="WholeValueLink"/> for why this has to happen at all.
        /// </summary>
        static string Unlink(string value)
        {
            var link = WholeValueLink.Match(value);
            if (link.Success)
                return link.Groups["text"].Value.Trim();

            var autolink = WholeValueAutolink.Match(value);
            return autolink.Success ? autolink.Groups["text"].Value.Trim() : value;
        }

        /// <summary>
        /// Drops the backslashes Docs' export put in front of punctuation. `Fix the sign\-in flow`
        /// is the title someone typed as `Fix the sign-in flow`, and without this the two never
        /// agree again: <c>doctor</c> compares the Sheet's title against the document's and reports
        /// drift on a ticket nobody touched, while <c>reindex</c> — which takes area and owner from
        /// the document — would write the backslash into the Sheet.
        ///
        /// The loop settles the same way <see cref="Unlink"/>'s does. We read the plain text and
        /// write plain text back; Docs re-escapes it on the next import, and the read after that
        /// strips it again. Nothing accumulates, and nothing needs escaping on the way out, because
        /// `\-` and `-` import to the same character.
        ///
        /// Scalars only, never the body. Below the bullets is the author's, the escapes render as
        /// they meant, and rewriting prose to taste is how an edit gets eaten (invariant 9).
        /// </summary>
        static string Unescape(string value)
        {
            if (!value.Contains('\\', StringComparison.Ordinal))
                return value;

            var unescaped = new StringBuilder(value.Length);

            for (var at = 0; at < value.Length; at++)
            {
                // A backslash before anything else — or trailing — is a literal one somebody typed.
                if (value[at] == '\\'
                    && at + 1 < value.Length
                    && EscapablePunctuation.Contains(value[at + 1], StringComparison.Ordinal))
                {
                    at++;
                }

                unescaped.Append(value[at]);
            }

            return unescaped.ToString();
        }

        static Ticket ToTicket(IDictionary<string, string> fields)
        {
            var ticket = new Ticket();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? Take(string key)
            {
                known.Add(key);
                return fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
            }

            ticket.Id = Take(SheetSchema.Id) ?? throw new FormatException($"Ticket document is missing the required '{SheetSchema.Id}' field.");
            ticket.Title = Take(SheetSchema.Title) ?? throw new FormatException($"Ticket '{ticket.Id}' is missing the required '{SheetSchema.Title}' field.");
            ticket.Type = Vocabulary.Parse<TicketType>(Take(SheetSchema.Type) ?? "feature", SheetSchema.Type);
            ticket.Area = Take(SheetSchema.Area) ?? string.Empty;
            ticket.Owner = Take(SheetSchema.Owner);

            ticket.Score = new WsjfScore
            {
                BusinessValue = ParseScore(Take(SheetSchema.BusinessValue), SheetSchema.BusinessValue),
                TimeCriticality = ParseScore(Take(SheetSchema.TimeCriticality), SheetSchema.TimeCriticality),
                RiskReductionOpportunityEnablement = ParseScore(Take(SheetSchema.RiskOpportunity), SheetSchema.RiskOpportunity),
                JobSize = ParseScore(Take(SheetSchema.JobSize), SheetSchema.JobSize)
            };

            foreach (var field in fields)
            {
                if (!known.Contains(field.Key))
                    ticket.ExtraFields[field.Key] = field.Value;
            }

            return ticket;
        }

        /// <summary>
        /// Only fields a person would sensibly edit by hand, and not the id or the title — those
        /// are the heading.
        ///
        /// Area and owner stay here even though the Sheet also carries them. They are content, not
        /// bookkeeping: <c>reindex</c> rebuilds a damaged row's content from the document, so
        /// dropping them would leave the Sheet as their only copy — the one thing the repair path
        /// exists to survive. They are also the first things a reader wants; a ticket that does not
        /// say who owns it is a worse document.
        ///
        /// Deliberately absent: timestamps, phase, and work state. Those are machine bookkeeping,
        /// and hand-maintaining ISO-8601 is hostile while a hand-edited `phase` would desync from
        /// the tab that actually defines it. The Sheet owns them, Drive's own createdTime and
        /// modifiedTime back up the first two, and the Activity Log records every lifecycle event
        /// in prose.
        /// </summary>
        static IEnumerable<KeyValuePair<string, string>> ToFields(Ticket ticket)
        {
            var fields = new List<KeyValuePair<string, string>>();

            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    fields.Add(new KeyValuePair<string, string>(key, value));
            }

            Add(SheetSchema.Type, Vocabulary.ToWire(ticket.Type));
            Add(SheetSchema.Area, ticket.Area);
            Add(SheetSchema.Owner, ticket.Owner);
            Add(SheetSchema.BusinessValue, Format(ticket.Score.BusinessValue));
            Add(SheetSchema.TimeCriticality, Format(ticket.Score.TimeCriticality));
            Add(SheetSchema.RiskOpportunity, Format(ticket.Score.RiskReductionOpportunityEnablement));
            Add(SheetSchema.JobSize, Format(ticket.Score.JobSize));

            foreach (var extra in ticket.ExtraFields)
                Add(extra.Key, extra.Value);

            return fields;
        }

        /// <summary>
        /// A title is untrusted input and a heading is one line, so a newline in it would split the
        /// document rather than fail — the tail would parse as body, or as nothing. Collapsing is
        /// the honest fix: every character survives, and a title spanning lines was never going to
        /// render as one anyway.
        /// </summary>
        static string SingleLine(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var collapsed = new StringBuilder(value.Length);
            var pending = false;

            foreach (var character in value)
            {
                if (character is '\n' or '\r' or '\t')
                {
                    pending = true;
                    continue;
                }

                if (pending && collapsed.Length > 0)
                    collapsed.Append(' ');

                pending = false;
                collapsed.Append(character);
            }

            return collapsed.ToString().Trim();
        }

        static string? Format(int? value) => value?.ToString(CultureInfo.InvariantCulture);

        static int? ParseScore(string? text, string field)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw new FormatException($"'{text}' in '{field}' is not a whole number.");

            // Surfaces as a malformed *document* rather than a bad argument, so doctor can report
            // the file and carry on instead of aborting the whole sweep.
            if (!WsjfScore.AllowedValues.Contains(parsed))
            {
                throw new FormatException(
                    $"'{text}' in '{field}' is off the WSJF scale. Use one of {string.Join(", ", WsjfScore.AllowedValues)}.");
            }

            return parsed;
        }

        /// <summary>
        /// The prose a new ticket starts with. No heading — <see cref="Serialize"/> writes that from
        /// the ticket, so putting one here would give the document two.
        ///
        /// Emphasis is `*asterisks*`, not `_underscores_`, because Docs renormalises the second to
        /// the first on import. Writing what the export already produces means a new ticket's body
        /// survives its first save unchanged rather than coming back subtly rewritten.
        ///
        /// Both editable sections fall back to `*TODO*` when nothing was given, because filing
        /// fast is worth keeping — but a placeholder is a promise to come back, so the CLI says
        /// which ones are still outstanding and both can be filled in later without opening Docs.
        /// </summary>
        public static string BuildInitialBody(Ticket ticket, string? description, string? acceptanceCriteria, TimeZoneInfo? zone = null)
        {
            var builder = new StringBuilder();

            builder.Append("## Description\n\n");
            builder.Append(string.IsNullOrWhiteSpace(description) ? "*TODO*" : description.Trim()).Append("\n\n");
            builder.Append("## Acceptance Criteria\n\n");
            builder.Append(string.IsNullOrWhiteSpace(acceptanceCriteria) ? UnwrittenCriteria : acceptanceCriteria.Trim()).Append("\n\n");
            builder.Append("## Notes\n\n");
            builder.Append("## Activity Log\n\n");
            builder.Append("- ").Append(SheetTime.FormatWithZone(ticket.Created, zone ?? TimeZoneInfo.Utc)).Append(" — created\n");

            return builder.ToString();
        }

        /// <summary>The body sections the CLI will rewrite. See <see cref="ReplaceSection"/>.</summary>
        public const string DescriptionHeading = "Description";

        /// <summary>
        /// The second one. It is here for the reason the first is: without a CLI path to it, the
        /// only way to write acceptance criteria was to open the document in Docs — so a ticket
        /// filed by an agent kept its placeholder, and every reader downstream was told a ticket
        /// was ready when nobody had said what "done" meant.
        /// </summary>
        public const string AcceptanceCriteriaHeading = "Acceptance Criteria";

        /// <summary>
        /// What the Acceptance Criteria section says until someone writes it. A checklist item
        /// rather than bare prose, so the section starts in the shape it is meant to end up in.
        /// </summary>
        public const string UnwrittenCriteria = "- [ ] *TODO*";

        /// <summary>
        /// Replaces the text under one heading, and touches nothing else in the body.
        ///
        /// This is the exception to "the store never rewrites prose", and it is deliberately the
        /// *narrowest* one that closes the gap: without it a description could only be seeded at
        /// <c>new</c> and never corrected from the CLI. It is bounded the same way
        /// <see cref="AppendActivity"/> is — by a heading a person can see. The section ends at the
        /// next heading of the same level or higher, so Notes, the Activity Log, and any section a
        /// human added come through byte-identical, and a `###` subheading *inside* the section is
        /// part of it rather than the end of it.
        ///
        /// It is used for exactly two headings, <see cref="DescriptionHeading"/> and
        /// <see cref="AcceptanceCriteriaHeading"/>, and the caller names which — the sections the
        /// CLI is expected to author. Every other heading stays hands-off, and adding a third here
        /// means deciding again that a machine should be allowed to overwrite it.
        ///
        /// A missing heading inserts one at the top rather than failing or guessing at which
        /// existing section was meant. Insertion cannot eat anything, which is the property that
        /// matters: someone who renamed or deleted the section gets a visible new one, not a
        /// silently overwritten old one.
        ///
        /// The replacement is written plain. Docs re-escapes and re-links it on the next import
        /// exactly as it does everything else here (invariant 18), and this text is prose, so it
        /// is never unescaped on the way back — it renders as its author meant it.
        /// </summary>
        public static string ReplaceSection(string body, string heading, string text)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(heading);

            var lines = (body ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            var replacement = (text ?? string.Empty).Replace("\r\n", "\n").Trim('\n');

            FindSection(lines, heading, out var start, out var end);

            var rebuilt = new List<string>();

            if (start < 0)
            {
                rebuilt.Add($"## {heading}");
                rebuilt.Add(string.Empty);
                rebuilt.AddRange(replacement.Split('\n'));
                rebuilt.Add(string.Empty);
                rebuilt.AddRange(lines);

                return Join(rebuilt);
            }

            rebuilt.AddRange(lines[..(start + 1)]);
            rebuilt.Add(string.Empty);
            rebuilt.AddRange(replacement.Split('\n'));

            if (end < lines.Length)
            {
                rebuilt.Add(string.Empty);
                rebuilt.AddRange(lines[end..]);
            }

            return Join(rebuilt);
        }

        static string Join(IEnumerable<string> lines) => string.Join('\n', lines).Trim('\n') + "\n";

        /// <summary>
        /// Where a section starts and ends: <paramref name="start"/> is the index of its heading
        /// line, or -1 if the heading is not there, and <paramref name="end"/> is the exclusive
        /// index of the line that ends it.
        ///
        /// This is the one definition of where a section ends — "the next heading of the same
        /// level or higher, so a `###` inside it is part of it rather than the end of it" — and
        /// both <see cref="ReplaceSection"/> and <see cref="SectionOf"/> go through it. Two copies
        /// of that rule would be two chances to disagree about which lines belong to a human,
        /// which is the mistake the rule exists to prevent.
        /// </summary>
        static void FindSection(string[] lines, string heading, out int start, out int end)
        {
            start = -1;
            end = lines.Length;

            var level = 0;

            for (var index = 0; index < lines.Length && start < 0; index++)
            {
                var found = HeadingLevel(lines[index]);

                if (found > 0 && string.Equals(HeadingText(lines[index], found), heading, StringComparison.OrdinalIgnoreCase))
                {
                    start = index;
                    level = found;
                }
            }

            if (start < 0)
                return;

            // A heading of the same level or higher ends the section; a deeper one is inside it.
            for (var index = start + 1; index < lines.Length; index++)
            {
                var found = HeadingLevel(lines[index]);

                if (found > 0 && found <= level)
                {
                    end = index;
                    return;
                }
            }
        }

        /// <summary>
        /// One section of the body, its heading included, or null when the body has no such
        /// heading. Read-only, and the counterpart to <see cref="ReplaceSection"/>: it uses the
        /// same boundaries, so what comes back is exactly what a replacement would overwrite.
        ///
        /// It exists so that reading before a write does not cost the whole document. A prose
        /// option replaces an entire section, so the caller has to know what is already there —
        /// but only for the section it is about to rewrite, and on a long-lived ticket the
        /// Activity Log is most of the body.
        /// </summary>
        public static string? SectionOf(string body, string heading)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(heading);

            var lines = (body ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            FindSection(lines, heading, out var start, out var end);

            return start < 0 ? null : Join(lines[start..end]);
        }

        /// <summary>
        /// The headings the body actually has, in order, so a caller who asked for a section that
        /// is not there can be told what is. A ticket may carry sections a person added, so the
        /// answer has to come from the document rather than from a list of the ones we know about.
        /// </summary>
        public static IReadOnlyList<string> HeadingsOf(string body)
        {
            var headings = new List<string>();

            foreach (var line in (body ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                var level = HeadingLevel(line);

                if (level > 0)
                    headings.Add(HeadingText(line, level));
            }

            return headings;
        }

        /// <summary>The headings <see cref="SectionOf"/> is asked for by name most often.</summary>
        public const string NotesHeading = "Notes";

        /// <summary>See <see cref="AppendActivity"/>; spelled without the `##` for section lookup.</summary>
        public const string ActivityLogHeading = "Activity Log";

        /// <summary>
        /// The body with all but the last <paramref name="keep"/> Activity Log entries replaced by
        /// a line saying how many were dropped.
        ///
        /// **This is for display only, and must never reach a write.** The log is the narrative
        /// record of a ticket's life and the only place lifecycle events are kept in prose; a
        /// trimmed body sent to <c>UpdateDocAsync</c> would destroy history that nothing else
        /// holds. It is called from the <c>show</c> command and nowhere else, and it stays that
        /// way — invariant 9 exists because eating a human's writing is the one unrecoverable
        /// failure here, and silently discarding the log would be exactly that.
        ///
        /// An entry is a line beginning with "- "; a line that does not is a continuation of the
        /// entry above it, so an entry a person wrapped — or that came back from Docs reflowed —
        /// is never cut in half.
        /// </summary>
        public static string TrimActivityLog(string body, int keep)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(keep);

            var lines = (body ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            FindSection(lines, ActivityLogHeading, out var start, out var end);

            if (start < 0)
                return Join(lines);

            // Index of the first line of each entry, in order.
            var entries = new List<int>();

            for (var index = start + 1; index < end; index++)
            {
                if (lines[index].StartsWith("- ", StringComparison.Ordinal))
                    entries.Add(index);
            }

            if (entries.Count <= keep)
                return Join(lines);

            var dropped = entries.Count - keep;
            var firstKept = entries[dropped];

            var rebuilt = new List<string>();
            rebuilt.AddRange(lines[..(start + 1)]);
            rebuilt.Add(string.Empty);
            rebuilt.Add($"- *… {dropped} earlier {(dropped == 1 ? "entry" : "entries")}; run `backlog show <id> --full` for all of them.*");
            rebuilt.AddRange(lines[firstKept..]);

            return Join(rebuilt);
        }

        /// <summary>
        /// The `#` count of a markdown heading line, or 0 for a line that is not one. The space is
        /// required, as it is in markdown itself — `#hashtag` opening a paragraph is not a heading,
        /// and treating it as one would end a section in the middle of somebody's sentence.
        /// </summary>
        static int HeadingLevel(string line)
        {
            var text = line.TrimStart();
            var hashes = 0;

            while (hashes < text.Length && text[hashes] == '#')
                hashes++;

            return hashes > 0 && hashes < text.Length && text[hashes] == ' ' ? hashes : 0;
        }

        static string HeadingText(string line, int level) => line.TrimStart()[level..].Trim();

        const string ActivityHeading = "## " + ActivityLogHeading;

        /// <summary>
        /// Appends a log entry rendered in the backlog's timezone. This is prose for people, never
        /// parsed back, so it gets the readable local form rather than UTC.
        /// </summary>
        public static string AppendActivity(string body, DateTimeOffset when, string note, TimeZoneInfo? zone = null)
        {
            var normalized = (body ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');
            var entry = $"- {SheetTime.FormatWithZone(when, zone ?? TimeZoneInfo.Utc)} — {note.Trim()}";

            if (!normalized.Contains(ActivityHeading, StringComparison.Ordinal))
                return $"{normalized}\n\n{ActivityHeading}\n\n{entry}\n";

            return $"{normalized}\n{entry}\n";
        }
    }
}
