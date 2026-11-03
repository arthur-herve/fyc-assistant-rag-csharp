// La documentation ne cite que des tests qui existent. Un nom de test cité par une page (README, docs/, exercices/…)
// est une promesse : l'apprenant le cherche dans le code. Un test renommé ou supprimé sans que la page suive le laisse
// chercher en vain, par exemple un corrigé d'exercice qui cite un test d'architecture disparu.

using System.Text.RegularExpressions;
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
    // Un test xUnit est une phrase dont les mots sont liés par « _ », comme Answers_with_cited_sources. Un test du
    // service IA, dans tests_python/, commence par test_.
    private static readonly Regex TestName = new(@"^(?:[A-Z][a-z0-9]*(?:_[a-z0-9]+){2,}|test_\w+)$");
    private static readonly Regex DefinedInCSharp = new(@"\b(?:void|Task)\s+(\w+)\s*\(");
    private static readonly Regex DefinedInPython = new(@"^\s*(?:async\s+)?def\s+(test_\w+)\s*\(", RegexOptions.Multiline);

    /// <summary>
    /// Les noms de tests que cite une page. Seul ce qu'elle écrit comme du code est lu, pas la prose. Un nom compte
    /// quand il est le dernier élément de ce qui est écrit : seul, après « :: » ou après une classe, comme
    /// « ArchitectureTests.Application_depends_only_on_the_domain » ou « Classe.test_x ». Un module ou un fichier,
    /// comme « tests_python/ai_service/test_server.py », n'en est pas un.
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

    /// <summary>
    /// Les tests cités par un .md que le dépôt ne définit nulle part, ni dans ses .cs, programmes à part compris, ni
    /// dans les .py du service IA.
    /// </summary>
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
        var missing = MissingTests(Repository.Root);
        Assert.True(missing.Count == 0, string.Join("\n", missing));   // en entier : Assert.Empty tronque chaque ligne
    }

    [Fact]
    public void The_check_reads_what_a_page_writes_as_code()
    {
        // Le test du test, sur un faux dépôt. La page cite trois tests absents : un test d'architecture remplacé sans
        // que la page suive, comme cela arrive à un corrigé, un test retiré de sa classe, et un test renommé cité dans
        // un bloc de code. Les tests d'un programme à part et ceux du service IA comptent comme définis. Un fichier, un
        // module Python comme tests_python.ai_service.test_server et la prose ne sont pas pris pour des noms de tests.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("exemples/jouet"));
        Directory.CreateDirectory(dir.File("tests_python"));
        File.WriteAllText(dir.File("exemples/jouet/RegleTests.cs"),
            "public class RegleTests\n{\n    [Fact]\n    public void Application_depends_only_on_the_domain() { }\n}\n");
        File.WriteAllText(dir.File("tests_python/test_service.py"), "class ServiceTest:\n    def test_health_answers(self):\n        pass\n");
        File.WriteAllText(dir.File("README.md"),
            "| 5 | `exemples/jouet/RegleTests.cs` | `RegleTests.Application_depends_only_on_the_domain`, `Only_the_composition_root_knows_it` |\n"
            + "Retiré : `RegleTests.Removed_from_the_class_since`. Prose_qui_n_est_pas_lue.\n"
            + "Le service : `tests_python/test_service.py::ServiceTest::test_health_answers`, `python -m unittest tests_python.ai_service.test_server`.\n"
            + "```bash\ndotnet test --filter \"FullyQualifiedName~Renamed_since_then_in_the_code\"\n```\n");
        Assert.Equal(
            new[] { "Only_the_composition_root_knows_it", "Removed_from_the_class_since", "Renamed_since_then_in_the_code" }
                .Select(name => $"README.md cite {name}, que le code ne définit pas"),
            MissingTests(dir.Path));
    }
}
