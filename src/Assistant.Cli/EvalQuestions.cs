// Jeux de questions d'évaluation (eval/questions*.json), partagés avec la version Python.
//
// Une question dit ce qu'on attend d'elle : les documents qui devraient répondre, des
// mots-clés grossiers, si elle est répondable, et les documents que l'utilisateur n'a
// PAS le droit de voir (pour compter les fuites d'accès, qui doivent rester à zéro).

using System.Text.Json.Nodes;
using Assistant.Application;

namespace Assistant.Cli;

public sealed record EvalQuestion(
    string Id,
    string UserName,
    string Question,
    IReadOnlyList<string> ExpectedDocuments,
    IReadOnlyList<string> ExpectedKeywords,
    bool Answerable,
    IReadOnlyList<string> ForbiddenDocuments)
{
    public SnapshotQuestion ToSnapshotQuestion(AppConfig config) => new(Id, config.User(UserName), Question);
}

public static class EvalQuestions
{
    public static List<EvalQuestion> Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var questions = root["questions"]?.AsArray() ?? throw new FormatException($"{path} : clé « questions » absente");
        return questions.Select(q => new EvalQuestion(
            q!["id"]!.GetValue<string>(),
            q["user"]?.GetValue<string>() ?? "alice",
            q["question"]!.GetValue<string>(),
            Strings(q["expected_documents"]),
            Strings(q["expected_keywords"]),
            q["answerable"]?.GetValue<bool>() ?? true,
            Strings(q["forbidden_documents"]))).ToList();
    }

    /// <summary>Un chemin relatif introuvable depuis le dossier courant est cherché depuis la racine du projet.</summary>
    public static string Resolve(string path) =>
        !Path.IsPathRooted(path) && !File.Exists(path) && File.Exists(Path.Combine(AppConfig.ProjectRoot, path))
            ? Path.Combine(AppConfig.ProjectRoot, path)
            : path;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();
}
