// Jeux de questions d'évaluation (eval/questions*.json), partagés avec la version Python.
//
// Une question dit ce qu'on attend d'elle : les documents qui devraient répondre, des
// mots-clés grossiers, si elle est répondable, et les documents que l'utilisateur n'a
// PAS le droit de voir (pour compter les fuites d'accès, qui doivent rester à zéro).

using System.Text.Json;
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
    /// <summary>
    /// Clés d'une question. Une faute de frappe (« expected_document ») ne doit pas vider une attente
    /// en silence : une clé inconnue est refusée.
    /// </summary>
    private static readonly string[] Keys =
        { "id", "user", "question", "answerable", "expected_documents", "forbidden_documents", "expected_keywords" };

    /// <summary>
    /// Un jeu de questions d'évaluation : UTF-8 strict (BOM accepté), JSON strict (clé en double, NaN, plus de 64 niveaux,
    /// chaîne qui n'est pas du texte), clés « _… » ignorées (des commentaires). Tout défaut est dit avec le fichier, puis
    /// la question (son numéro) et le champ quand il y en a un ; un défaut du JSON lui-même, repéré à sa lecture, est dit
    /// sans la question (avec la clé, pour une clé en double).
    /// </summary>
    public static List<EvalQuestion> Load(string path)
    {
        List<EvalQuestion> questions;
        try
        {
            // Fichier absent : IOException, que la ligne de commande signale. Puis UTF-8 strict (BOM accepté) et JSON
            // strict : une erreur de syntaxe, NaN compris, est une JsonException ; une clé en double, plus de 64 niveaux,
            // un « \ud800 » isolé, même dans un commentaire (GetString lèverait sinon une InvalidOperationException, une
            // trace), une FormatException.
            using var document = JsonText.ParseDocument(TextFiles.ReadUtf8(path), strings: true);
            questions = Parse(document.RootElement);
        }
        catch (JsonException error)
        {
            throw new FormatException($"{path} : jeu de questions mal formé (JSON invalide : {error.Message})", error);
        }
        catch (FormatException error)
        {
            throw new FormatException($"{path} : jeu de questions mal formé ({error.Message})", error);
        }
        return questions.Count > 0 ? questions : throw new FormatException($"aucune question dans {path}");
    }

    /// <summary>
    /// --limit N : les N premières questions (toutes sans --limit), pour le banc, les expériences et snapshot record. N vaut
    /// au moins 1 : 0 ou un nombre négatif est refusé, pas ignoré.
    /// </summary>
    public static List<EvalQuestion> Limit(List<EvalQuestion> questions, int? limit) => limit switch
    {
        null => questions,
        < 1 => throw new ArgumentException($"--limit doit valoir au moins 1, pas {limit}"),
        _ => questions.Take(limit.Value).ToList(),
    };

    /// <summary>Un chemin relatif introuvable depuis le dossier courant est cherché depuis la racine du projet.</summary>
    public static string Resolve(string path) =>
        !Path.IsPathRooted(path) && !File.Exists(path) && File.Exists(Path.Combine(AppConfig.ProjectRoot, path))
            ? Path.Combine(AppConfig.ProjectRoot, path)
            : path;

    private static List<EvalQuestion> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("questions", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("un objet JSON avec une liste « questions » est attendu");
        }
        var questions = items.EnumerateArray().Select((item, i) => Question(item, i + 1)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < questions.Count; i++)
        {
            // Un identifiant sert de clé (stabilité, instantanés, indicateurs) : deux questions ne le partagent pas.
            if (!seen.Add(questions[i].Id))
            {
                throw new FormatException($"question n° {i + 1} : identifiant « {questions[i].Id} » déjà utilisé");
            }
        }
        return questions;
    }

    /// <summary>Une question relue, types vérifiés : "false" n'est pas un booléen, "teletravail" pas une liste de documents.</summary>
    private static EvalQuestion Question(JsonElement item, int number)
    {
        var where = $"question n° {number}";
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"{where} : un objet JSON est attendu");
        }
        // Une clé qui commence par « _ » est un commentaire, comme dans la configuration (JSON n'en a pas).
        var unknown = item.EnumerateObject().Select(p => p.Name).Where(k => !Keys.Contains(k) && !k.StartsWith('_'))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new FormatException($"{where} : clé(s) inconnue(s) [{string.Join(", ", unknown)}] "
                                      + $"(connues : {string.Join(", ", Keys.Order(StringComparer.Ordinal))})");
        }
        var id = Text(item, "id") ?? throw new FormatException($"{where} : champ « id » manquant ou non textuel");
        var question = Text(item, "question") ?? throw new FormatException($"{where} : champ « question » manquant ou non textuel");
        var user = item.TryGetProperty("user", out _)
            ? Text(item, "user") ?? throw new FormatException($"{where} : « user » doit être un texte")
            : "alice";
        var answerable = !item.TryGetProperty("answerable", out var a) || a.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"{where} : « answerable » doit valoir true ou false"),
        };
        var expected = Strings(item, "expected_documents", where);
        var forbidden = Strings(item, "forbidden_documents", where);
        var keywords = Strings(item, "expected_keywords", where);
        return new EvalQuestion(id, user, question, expected, keywords, answerable, forbidden);
    }

    private static string? Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement item, string key, string where)
    {
        if (!item.TryGetProperty(key, out var value))
        {
            return Array.Empty<string>();
        }
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
        {
            throw new FormatException($"{where} : « {key} » doit être une liste de textes");
        }
        return value.EnumerateArray().Select(v => v.GetString()!).ToList();
    }
}
