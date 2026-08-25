using System.Reflection;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Mcp.Tests
{
    /// <summary>
    /// What this front end is allowed to see.
    ///
    /// The rule is the same one `Noogen.Backlog.Tests` states for the other assemblies: a front end
    /// is one way of delivering the surface, not the surface. It may see the domain library and the
    /// verb layer; it may not see another front end. Anything this server turned out to need from
    /// the CLI would mean the line between "what every caller needs" and "what a terminal needs" is
    /// in the wrong place — and the reason not to add that reference is not written anywhere the
    /// compiler prints.
    ///
    /// It is asserted here rather than beside the others because the fakes live in that project and
    /// this one references it; the dependency has to run one way.
    /// </summary>
    public class AssemblyBoundaryTests
    {
        static Assembly Server => typeof(BacklogTool).Assembly;

        static IReadOnlyList<string> ReferencesOf(Assembly assembly) =>
            [.. assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

        [Fact]
        public void Server_IsNotAllowedToKnowAboutTheCommandLine() =>
            Assert.DoesNotContain("Noogen.Backlog.Cli", ReferencesOf(Server), StringComparer.Ordinal);

        [Fact]
        public void Server_SitsOnTheDomainAndTheVerbLayer()
        {
            var references = ReferencesOf(Server);

            Assert.Contains("Noogen.Backlog", references, StringComparer.Ordinal);
            Assert.Contains("Noogen.Backlog.Verbs", references, StringComparer.Ordinal);
        }

        /// <summary>
        /// Invariant 16: one embedded copy of the skill, in the layer that describes the verbs it
        /// teaches. This server serves those bytes; it does not carry a second set.
        /// </summary>
        [Fact]
        public void Server_CarriesNoSkillOfItsOwn() =>
            Assert.DoesNotContain(
                Server.GetManifestResourceNames(),
                resource => resource.StartsWith(EmbeddedSkill.ResourcePrefix, StringComparison.Ordinal));
    }
}
