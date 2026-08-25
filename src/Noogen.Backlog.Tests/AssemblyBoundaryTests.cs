using System.Reflection;
using Noogen.Backlog.Verbs;

namespace Noogen.Backlog.Tests
{
    /// <summary>
    /// Which assembly may see which. The project references already enforce this — the point of
    /// asserting it again is that a reference is easy to add and the reason it must not be added is
    /// not written anywhere the compiler prints.
    ///
    /// The rule: `Noogen.Backlog` holds what every front end needs — the operations, the JSON
    /// shapes, the name of a failure. `Noogen.Backlog.Verbs` holds what only a *text-driven* front
    /// end needs — the verb names, the options, the help. A REST API is resource-shaped and will
    /// reference the first and not the second, which is the check that the line is in the right
    /// place: anything the domain library turns out to need from the verb layer was misfiled.
    /// </summary>
    public class AssemblyBoundaryTests
    {
        static IReadOnlyList<string> ReferencesOf(Assembly assembly) =>
            [.. assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

        static Assembly Domain => typeof(BacklogApi).Assembly;

        static Assembly VerbLayer => typeof(VerbCatalog).Assembly;

        [Fact]
        public void Domain_IsNotAllowedToKnowAboutVerbsOrAnyFrontEnd()
        {
            var references = ReferencesOf(Domain);

            Assert.DoesNotContain("Noogen.Backlog.Verbs", references, StringComparer.Ordinal);
            Assert.DoesNotContain("Noogen.Backlog.Cli", references, StringComparer.Ordinal);
        }

        /// <summary>
        /// The verb layer describes a surface; it does not implement one. A front end referencing
        /// it is expected — it referencing a front end would mean the description had grown a
        /// dependency on one particular way of delivering it.
        /// </summary>
        [Fact]
        public void VerbLayer_IsNotAllowedToKnowAboutAnyFrontEnd()
        {
            Assert.DoesNotContain("Noogen.Backlog.Cli", ReferencesOf(VerbLayer), StringComparer.Ordinal);
        }

        [Fact]
        public void VerbLayer_SitsOnTheDomainRatherThanBesideIt() =>
            Assert.Contains("Noogen.Backlog", ReferencesOf(VerbLayer), StringComparer.Ordinal);

        /// <summary>
        /// The two live in different assemblies but one namespace tree, so a reader can tell which
        /// side of the line a type is on without opening a project file.
        /// </summary>
        [Fact]
        public void VerbLayer_TypesAreNamespacedApartFromTheDomain()
        {
            foreach (var type in VerbLayer.GetExportedTypes())
                Assert.Equal("Noogen.Backlog.Verbs", type.Namespace);
        }

        [Fact]
        public void Domain_HoldsTheContractEveryFrontEndEmits()
        {
            var names = Domain.GetExportedTypes().Select(type => type.Name).ToList();

            Assert.Contains("BacklogApi", names, StringComparer.Ordinal);
            Assert.Contains("TicketView", names, StringComparer.Ordinal);
            Assert.Contains("BacklogJson", names, StringComparer.Ordinal);
            Assert.Contains("BacklogFault", names, StringComparer.Ordinal);
        }

        /// <summary>
        /// The skill teaches the verbs, so it travels with them — and it is embedded once, not per
        /// front end, which is what invariant 16 is about.
        /// </summary>
        [Fact]
        public void VerbLayer_CarriesTheOnlyCopyOfTheSkill()
        {
            Assert.True(EmbeddedSkill.IsEmbedded);
            Assert.Contains(EmbeddedSkill.EntryFileName, EmbeddedSkill.Files.Select(file => file.RelativePath), StringComparer.Ordinal);

            Assert.DoesNotContain(
                Domain.GetManifestResourceNames(),
                resource => resource.StartsWith(EmbeddedSkill.ResourcePrefix, StringComparison.Ordinal));
        }
    }
}
