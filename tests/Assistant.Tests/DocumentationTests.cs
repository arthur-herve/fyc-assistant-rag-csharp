// La documentation ne cite que des tests qui existent. Un nom de test cité par une page (README, docs/, exercices/,
// cas-pratique/…) est une promesse : l'apprenant le cherche dans le code. Un test renommé ou supprimé sans que la page
// suive le laisse chercher en vain, par exemple un corrigé d'exercice qui cite un test d'architecture disparu.

using System.Text.RegularExpressions;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public class DocumentationTests
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "__pycache__", "data",   // ni du dépôt, ni de la documentation
    };
    private static readonly Regex Code = new(@"```.*?```|`[^`\n]+`", RegexOptions.Singleline);   // bloc, ou entre accents graves
    private static readonly Regex Dotted = new(@"[\w.:/]+");   // un nom, pointé ou non, ou un chemin
    private static readonly Regex Separator = new("(::|[.:/])");
    // Un test xUnit est une phrase aux mots liés par « _ » (Answers_with_cited_sources) ; un test du service IA
    // (tests_python/) commence par test_.
    private static readonly Regex TestName = new(@"^(?:[A-Z][a-z0-9]*(?:_[a-z0-9]+){2,}|test_\w+)$");
    private static readonly Regex DefinedInCSharp = new(@"\b(?:void|Task)\s+(\w+)\s*\(");
    private static readonly Regex DefinedInPython = new(@"^\s*(?:async\s+)?def\s+(test_\w+)\s*\(", RegexOptions.Multiline);

    /// <summary>
    /// Les noms de tests que cite une page, dans ce qu'elle écrit comme du code (la prose n'est pas lue) : un nom qui
    /// finit l'écriture, seul, après « :: » ou après une classe (« ArchitectureTests.Application_depends_only_on_the_domain »,
    /// « Classe.test_x ») ; pas un module ni un fichier (« tests_python/ai_service/test_server.py »).
    /// </summary>
    internal static SortedSet<string> CitedTests(string markdown)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var code = string.Join(" ", Code.Matches(markdown).Select(m => m.Value));
        foreach (var dotted in Dotted.Matches(code).Select(m => m.Value))
        {
            var parts = Separator.Split(dotted);   // nom, séparateur, nom… : « a.B.c » donne a . B . c
            var last = parts[^1];
            var before = parts.Length > 1 ? parts[^2] : "";
            var owner = parts.Length > 2 ? parts[^3] : "";
            if (TestName.IsMatch(last) && (before is "" or "::" || (before == "." && owner.Length > 0 && char.IsUpper(owner[0]))))
            {
                names.Add(last);
            }
        }
        return names;
    }

    /// <summary>Chaque test cité par un .md et défini nulle part dans le dépôt (.cs des tests et des kits, .py du service IA).</summary>
    internal static List<string> MissingTests(string root)
    {
        IEnumerable<string> Files(string pattern) => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                .Any(Skipped.Contains))
            .Order(StringComparer.Ordinal);
        var defined = Files("*.cs").SelectMany(path => DefinedInCSharp.Matches(File.ReadAllText(path)))
            .Concat(Files("*.py").SelectMany(path => DefinedInPython.Matches(File.ReadAllText(path))))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        return Files("*.md")
            .SelectMany(page => CitedTests(File.ReadAllText(page)).Where(name => !defined.Contains(name))
                .Select(name => $"{Path.GetRelativePath(root, page).Replace('\\', '/')} cite {name}, que le code ne définit pas"))
            .ToList();
    }

    [Fact]
    public void Every_test_cited_by_the_documentation_exists()
    {
        var missing = MissingTests(AppConfig.ProjectRoot);
        Assert.True(missing.Count == 0, string.Join("\n", missing));   // en entier : Assert.Empty tronque chaque ligne
    }

    [Fact]
    public void The_check_reads_what_a_page_writes_as_code()
    {
        // Test du test : un test d'architecture remplacé sans que la page suive (le cas d'un corrigé), un test retiré
        // de sa classe, et un test renommé cité dans un bloc de code. Un test d'un kit d'exercices ou du service IA
        // compte ; un fichier, un module Python (tests_python.ai_service.test_server) ou la prose ne sont pas des noms
        // de tests.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("exercices/kit"));
        Directory.CreateDirectory(dir.File("tests_python"));
        File.WriteAllText(dir.File("exercices/kit/RegleTests.cs"),
            "public class RegleTests\n{\n    [Fact]\n    public void Application_depends_only_on_the_domain() { }\n}\n");
        File.WriteAllText(dir.File("tests_python/test_service.py"), "class ServiceTest:\n    def test_health_answers(self):\n        pass\n");
        File.WriteAllText(dir.File("README.md"),
            "| 5 | `exercices/kit/RegleTests.cs` | `RegleTests.Application_depends_only_on_the_domain`, `Only_the_composition_root_knows_it` |\n"
            + "Retiré : `RegleTests.Removed_from_the_class_since`. Prose_qui_n_est_pas_lue.\n"
            + "Le service : `tests_python/test_service.py::ServiceTest::test_health_answers`, `python -m unittest tests_python.ai_service.test_server`.\n"
            + "```bash\ndotnet test --filter \"FullyQualifiedName~Renamed_since_then_in_the_code\"\n```\n");
        Assert.Equal(
            new[] { "Only_the_composition_root_knows_it", "Removed_from_the_class_since", "Renamed_since_then_in_the_code" }
                .Select(name => $"README.md cite {name}, que le code ne définit pas"),
            MissingTests(dir.Path));
    }
}
