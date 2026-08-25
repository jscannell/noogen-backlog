using System.Reflection;
using System.Text;

namespace Noogen.Backlog
{
    /// <summary>
    /// The Claude Code skill that teaches an agent to drive this CLI, carried inside the tool.
    ///
    /// The skill and the binary are one thing: a skill describing verbs the installed tool does
    /// not have is worse than no skill at all. So they travel as a single artifact — the nupkg —
    /// and `backlog install-skill` unpacks the skill half. Nobody can end up with one updated and
    /// the other stale.
    ///
    /// The source of truth stays `.claude/skills/backlog` in the repository, which is also the
    /// copy this repository's own agents load. The build embeds that directory rather than a
    /// second copy, so there is nothing to keep in sync.
    ///
    /// It is embedded into this assembly rather than into a front end because more than one front
    /// end serves it: the CLI writes it to disk with `install-skill`, and the MCP server hands the
    /// same bytes to a caller with no skills directory to write to. Two embeddings would be a
    /// second copy again.
    /// </summary>
    public static class EmbeddedSkill
    {
        /// <summary>
        /// The prefix the build gives every skill resource — see the CLI csproj. MSBuild's
        /// %(RecursiveDir) uses the build machine's own separator, so a nested resource is named
        /// `skill/references\wsjf.md` on Windows and `skill/references/wsjf.md` elsewhere. Never
        /// assume one; both are split here and the relative path is normalised to forward slashes.
        /// </summary>
        public const string ResourcePrefix = "skill";

        public const string EntryFileName = "SKILL.md";

        static readonly char[] Separators = ['/', '\\'];

        static IReadOnlyList<SkillFile>? _files;

        /// <summary>Every file of the skill, in a stable order. Empty if the build embedded none.</summary>
        public static IReadOnlyList<SkillFile> Files => _files ??= Read(typeof(EmbeddedSkill).Assembly);

        public static bool IsEmbedded => Files.Count > 0;

        /// <summary>
        /// The directory to install under, read from the skill's own frontmatter rather than
        /// held as a constant here. Claude Code discovers a skill by its directory, so a rename
        /// that updated only one of the two would leave a second, stale copy alongside the first.
        /// </summary>
        public static string Name => ReadName(RequireEntry());

        /// <summary>
        /// Makes <paramref name="skillsRoot"/> carry this skill. Installs when nothing is there;
        /// otherwise refuses unless <paramref name="force"/>, because below that directory is a
        /// person's own Claude configuration and an edit of theirs is not ours to discard.
        ///
        /// Forcing makes the directory *match* — differing files are rewritten and files the tool
        /// does not carry are removed — so a reference dropped in a later version does not linger
        /// and keep teaching an agent something untrue.
        /// </summary>
        public static SkillInstallation Install(string skillsRoot, bool force)
        {
            if (string.IsNullOrWhiteSpace(skillsRoot))
                throw new UsageException("--path needs a directory to install into.");

            var destination = Path.Combine(skillsRoot, Name);
            var differences = Differences(destination);

            // An absent destination is never a conflict: nothing of anyone's is at risk. Only an
            // existing directory that already differs needs the person to say so.
            var blocked = !force && Directory.Exists(destination) && differences.Count > 0;
            if (blocked)
                return new SkillInstallation(destination, differences, [], [], false);

            var written = new List<string>();

            foreach (var difference in differences)
            {
                if (difference.Kind == SkillDifference.Extra)
                    continue;

                var file = Files.First(candidate => candidate.RelativePath == difference.Path);
                var path = Resolve(destination, file.RelativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Content);
                written.Add(file.RelativePath);
            }

            var removed = new List<string>();

            foreach (var difference in differences)
            {
                if (difference.Kind != SkillDifference.Extra)
                    continue;

                File.Delete(Resolve(destination, difference.Path));
                removed.Add(difference.Path);
            }

            Directory.CreateDirectory(destination);
            PruneEmptyDirectories(destination);

            return new SkillInstallation(destination, differences, written, removed, true);
        }

        /// <summary>
        /// How an installed copy differs from the one in this tool: files changed by hand, files
        /// gone missing, and files here that the tool does not carry.
        /// </summary>
        public static IReadOnlyList<SkillDifference> Differences(string destination)
        {
            var differences = new List<SkillDifference>();

            foreach (var file in Files)
            {
                var path = Resolve(destination, file.RelativePath);

                if (!File.Exists(path))
                    differences.Add(new SkillDifference(file.RelativePath, SkillDifference.Missing));
                else if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(file.Content))
                    differences.Add(new SkillDifference(file.RelativePath, SkillDifference.Changed));
            }

            foreach (var extra in Extras(destination))
                differences.Add(new SkillDifference(extra, SkillDifference.Extra));

            return differences;
        }

        static IReadOnlyList<string> Extras(string destination)
        {
            if (!Directory.Exists(destination))
                return [];

            var packaged = new HashSet<string>(
                Files.Select(file => file.RelativePath),
                StringComparer.OrdinalIgnoreCase);

            var extras = new List<string>();

            foreach (var path in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(destination, path).Replace('\\', '/');

                if (!packaged.Contains(relative))
                    extras.Add(relative);
            }

            extras.Sort(StringComparer.Ordinal);
            return extras;
        }

        static void PruneEmptyDirectories(string destination)
        {
            foreach (var directory in Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
        }

        static string Resolve(string destination, string relativePath) =>
            Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar));

        static SkillFile RequireEntry() =>
            Files.FirstOrDefault(file => string.Equals(file.RelativePath, EntryFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"This build of the tool carries no skill: no embedded {EntryFileName}. It was " +
                "built with BacklogSkillDirectory pointing somewhere without one.");

        static IReadOnlyList<SkillFile> Read(Assembly assembly)
        {
            var files = new List<SkillFile>();

            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var relative = RelativePathOf(resource);
                if (relative is null)
                    continue;

                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                    continue;

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);

                files.Add(new SkillFile(relative, buffer.ToArray()));
            }

            files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
            return files;
        }

        /// <summary>Null for any resource that is not part of the skill — oauth.json, say.</summary>
        static string? RelativePathOf(string resourceName)
        {
            var parts = resourceName.Split(Separators);

            return parts.Length >= 2 && string.Equals(parts[0], ResourcePrefix, StringComparison.Ordinal)
                ? string.Join("/", parts.Skip(1))
                : null;
        }

        static string ReadName(SkillFile entry)
        {
            using var reader = new StringReader(Encoding.UTF8.GetString(entry.Content).TrimStart('﻿'));

            var inFrontmatter = false;
            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.Trim();

                if (trimmed == "---")
                {
                    if (inFrontmatter)
                        break;

                    inFrontmatter = true;
                    continue;
                }

                if (!inFrontmatter)
                    break;

                if (!trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = trimmed["name:".Length..].Trim().Trim('"', '\'');

                // It becomes a directory name under someone's ~/.claude. Trusted input — it is
                // our own committed file — but one guard is cheaper than the failure it prevents.
                if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == ".." || name == ".")
                    throw new FormatException($"{EntryFileName} declares 'name: {name}', which cannot be a directory name.");

                return name;
            }

            throw new FormatException($"{EntryFileName} has no 'name:' in its frontmatter, so there is no directory to install it as.");
        }
    }

    /// <summary>One file of the skill as the build embedded it. Bytes, so it survives verbatim.</summary>
    public class SkillFile
    {
        public SkillFile(string relativePath, byte[] content)
        {
            RelativePath = relativePath;
            Content = content;
        }

        /// <summary>Forward-slashed and relative to the skill directory: `references/wsjf.md`.</summary>
        public string RelativePath { get; }

        public byte[] Content { get; }
    }

    public class SkillDifference
    {
        public const string Changed = "changed";
        public const string Missing = "missing";
        public const string Extra = "extra";

        public SkillDifference(string path, string kind)
        {
            Path = path;
            Kind = kind;
        }

        public string Path { get; }

        /// <summary>`changed`, `missing`, or `extra`. Part of the --json contract.</summary>
        public string Kind { get; }
    }

    public class SkillInstallation
    {
        public SkillInstallation(
            string path,
            IReadOnlyList<SkillDifference> differences,
            IReadOnlyList<string> written,
            IReadOnlyList<string> removed,
            bool applied)
        {
            Path = path;
            Differences = differences;
            Written = written;
            Removed = removed;
            Applied = applied;
        }

        public string Path { get; }

        public IReadOnlyList<SkillDifference> Differences { get; }

        public IReadOnlyList<string> Written { get; }

        public IReadOnlyList<string> Removed { get; }

        /// <summary>False when an existing copy differed and --force was not given.</summary>
        public bool Applied { get; }

        public bool UpToDate => Applied && Differences.Count == 0;
    }
}
