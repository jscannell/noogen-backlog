using System.Text;
using Noogen.Backlog.Cli;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// The build bakes the Claude Code skill into the CLI so the nupkg is the whole distribution.
    /// Like the OAuth client, this is a property of the assembly that carries it, so it can only
    /// be asserted from a test project that references that assembly.
    ///
    /// Every test installs into a temporary directory: the one place these must never touch is
    /// the developer's own ~/.claude.
    /// </summary>
    public class EmbeddedSkillTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "noogen-skill-" + Guid.NewGuid().ToString("N"));

        public EmbeddedSkillTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        string Destination => Path.Combine(_root, EmbeddedSkill.Name);

        [Fact]
        public void Files_AnyBuild_CarryTheSkillAndItsReferences()
        {
            // The skill is committed, so unlike the OAuth client its absence is a broken build
            // rather than a legitimate one.
            Assert.True(EmbeddedSkill.IsEmbedded);
            Assert.Contains(EmbeddedSkill.Files, file => file.RelativePath == EmbeddedSkill.EntryFileName);
            Assert.Contains(EmbeddedSkill.Files, file => file.RelativePath == "references/wsjf.md");
            Assert.Contains(EmbeddedSkill.Files, file => file.RelativePath == "references/writing-style.md");
        }

        [Fact]
        public void Files_NestedResource_UsesForwardSlashesWhicheverSeparatorTheBuildMachineUsed()
        {
            // MSBuild's %(RecursiveDir) emits the host separator, so the resource is literally
            // named `skill/references\wsjf.md` on Windows. A caller must never see that.
            Assert.All(EmbeddedSkill.Files, file => Assert.DoesNotContain('\\', file.RelativePath));
        }

        [Fact]
        public void Files_TheEmbeddedOAuthClient_IsNotMistakenForPartOfTheSkill()
        {
            // Both are embedded resources of the same assembly; only the prefix separates them.
            Assert.DoesNotContain(EmbeddedSkill.Files, file => file.RelativePath.Contains("oauth", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Name_Always_MatchesTheDirectoryTheSkillLivesInInTheRepository()
        {
            // Claude Code finds a skill by its directory, so the installed folder is named from
            // the skill's own frontmatter rather than a constant that could drift from it.
            Assert.Equal("backlog", EmbeddedSkill.Name);
        }

        [Fact]
        public void Install_NothingInstalled_WritesEveryFileUnderTheSkillsRoot()
        {
            var installation = EmbeddedSkill.Install(_root, force: false);

            Assert.True(installation.Applied);
            Assert.Equal(Destination, installation.Path);
            Assert.Equal(EmbeddedSkill.Files.Count, installation.Written.Count);

            foreach (var file in EmbeddedSkill.Files)
                Assert.Equal(file.Content, File.ReadAllBytes(Path.Combine(Destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        }

        [Fact]
        public void Install_AlreadyCurrent_ReportsUpToDateAndWritesNothing()
        {
            EmbeddedSkill.Install(_root, force: false);
            var written = LastWriteTimes();

            var installation = EmbeddedSkill.Install(_root, force: false);

            Assert.True(installation.UpToDate);
            Assert.Empty(installation.Written);
            Assert.Equal(written, LastWriteTimes());
        }

        [Fact]
        public void Install_SkillEditedByHand_RefusesAndNamesWhatDiffers()
        {
            EmbeddedSkill.Install(_root, force: false);

            var edited = Path.Combine(Destination, EmbeddedSkill.EntryFileName);
            File.WriteAllText(edited, "# mine now");

            var installation = EmbeddedSkill.Install(_root, force: false);

            Assert.False(installation.Applied);
            Assert.Empty(installation.Written);
            Assert.Equal("# mine now", File.ReadAllText(edited));

            var difference = Assert.Single(installation.Differences);
            Assert.Equal(EmbeddedSkill.EntryFileName, difference.Path);
            Assert.Equal(SkillDifference.Changed, difference.Kind);
        }

        [Fact]
        public void Install_FileDeletedByHand_RefusesAndReportsItMissing()
        {
            EmbeddedSkill.Install(_root, force: false);
            File.Delete(Path.Combine(Destination, "references", "wsjf.md"));

            var installation = EmbeddedSkill.Install(_root, force: false);

            Assert.False(installation.Applied);

            var difference = Assert.Single(installation.Differences);
            Assert.Equal("references/wsjf.md", difference.Path);
            Assert.Equal(SkillDifference.Missing, difference.Kind);
        }

        [Fact]
        public void Install_Forced_RestoresEveryFileToWhatTheToolCarries()
        {
            EmbeddedSkill.Install(_root, force: false);

            var edited = Path.Combine(Destination, EmbeddedSkill.EntryFileName);
            File.WriteAllText(edited, "# mine now");

            var installation = EmbeddedSkill.Install(_root, force: true);

            Assert.True(installation.Applied);
            Assert.Contains(EmbeddedSkill.EntryFileName, installation.Written);
            Assert.Equal(EmbeddedSkill.Files.First(file => file.RelativePath == EmbeddedSkill.EntryFileName).Content, File.ReadAllBytes(edited));
        }

        [Fact]
        public void Install_Forced_RemovesAFileTheToolNoLongerCarries()
        {
            // A reference dropped in a later version must not linger and keep teaching an agent
            // something that is no longer true.
            EmbeddedSkill.Install(_root, force: false);

            var stale = Path.Combine(Destination, "references", "removed-in-a-later-version.md");
            File.WriteAllText(stale, "old guidance");

            var installation = EmbeddedSkill.Install(_root, force: true);

            Assert.Contains("references/removed-in-a-later-version.md", installation.Removed);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(Path.Combine(Destination, "references", "wsjf.md")));
        }

        [Fact]
        public void Install_ExtraFilePresent_RefusesWithoutForceRatherThanDeletingIt()
        {
            EmbeddedSkill.Install(_root, force: false);

            var theirs = Path.Combine(Destination, "notes.md");
            File.WriteAllText(theirs, "my own notes");

            var installation = EmbeddedSkill.Install(_root, force: false);

            Assert.False(installation.Applied);
            Assert.True(File.Exists(theirs));

            var difference = Assert.Single(installation.Differences);
            Assert.Equal(SkillDifference.Extra, difference.Kind);
        }

        [Fact]
        public void Install_EmptyRoot_IsRefusedRatherThanWritingToTheWorkingDirectory()
        {
            Assert.Throws<UsageException>(() => EmbeddedSkill.Install("   ", force: false));
        }

        [Fact]
        public void SkillMarkdown_Always_StartsWithTheFrontmatterClaudeCodeNeeds()
        {
            var entry = EmbeddedSkill.Files.First(file => file.RelativePath == EmbeddedSkill.EntryFileName);
            var text = Encoding.UTF8.GetString(entry.Content);

            Assert.StartsWith("---", text, StringComparison.Ordinal);
            Assert.Contains("description:", text, StringComparison.Ordinal);
        }

        Dictionary<string, DateTime> LastWriteTimes() =>
            Directory.EnumerateFiles(Destination, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.GetLastWriteTimeUtc);
    }
}
