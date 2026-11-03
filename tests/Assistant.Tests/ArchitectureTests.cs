// La règle de dépendance du découpage, vérifiée de deux façons. Les .csproj déclarent les références entre
// projets. Les assemblies compilés montrent ce que le code utilise vraiment, quelle que soit la façon dont les
// sources l'écrivent.

using System.Xml.Linq;
using Assistant.Application;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

/// <summary>La règle de dépendance du découpage, vérifiée sur les .csproj et sur les assemblies compilés.</summary>
public class ArchitectureTests
{
    private static IEnumerable<string> References(Type anyTypeOfAssembly) =>
        anyTypeOfAssembly.Assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static readonly HashSet<string> DomainAllowed = new()
    {
        "System.Runtime", "System.Collections", "System.Linq", "System.Text.RegularExpressions", "System.Memory", "netstandard",
        "System.Text.Json",   // bibliothèque standard, permise au cœur
    };

    /// <summary>Les projets que référence <c>src/&lt;projet&gt;/&lt;projet&gt;.csproj</c>, triés, sur une ligne.</summary>
    private static string ProjectReferences(string project)
    {
        var csproj = XDocument.Load(Path.Combine(Repository.Root, "src", project, project + ".csproj"));
        var names = csproj.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(((string?)reference.Attribute("Include") ?? "").Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToList();
        return $"{project} : {(names.Count == 0 ? "aucune référence" : string.Join(", ", names))}";
    }

    [Fact]
    public void Each_project_references_only_the_next_layer_inward() =>
        // Un assembly compilé ne garde une référence que si le code s'en sert. Le .csproj, lui, déclare aussi celle
        // d'un projet encore vide : c'est donc lui qu'on lit ici.
        Assert.Equal(
            new[]
            {
                "Assistant.Domain : aucune référence",
                "Assistant.Application : Assistant.Domain",
                "Assistant.Infrastructure : Assistant.Application",
                "Assistant.Cli : Assistant.Infrastructure",
            },
            new[] { "Assistant.Domain", "Assistant.Application", "Assistant.Infrastructure", "Assistant.Cli" }.Select(ProjectReferences));

    [Fact]
    public void Domain_depends_on_nothing_but_the_runtime() =>
        // C'est une liste blanche : une référence à System.Net.Http dans le domaine ferait échouer ce test.
        Assert.All(References(typeof(Document)), name => Assert.Contains(name, DomainAllowed));

    [Fact]
    public void Application_depends_only_on_the_domain() =>
        Assert.All(References(typeof(PromptTemplate)).Where(n => !n.StartsWith("System", StringComparison.Ordinal)),
                   name => Assert.Equal("Assistant.Domain", name));

    [Fact]
    public void The_core_does_not_touch_the_network()
    {
        // Le réseau est un détail de l'infrastructure. JSON, lui, est permis au cœur, parce que System.Text.Json fait
        // partie de la bibliothèque standard.
        foreach (var core in new[] { typeof(PromptTemplate), typeof(Document) })
        {
            Assert.DoesNotContain(References(core), n => n.StartsWith("System.Net", StringComparison.Ordinal));
        }
    }
}
