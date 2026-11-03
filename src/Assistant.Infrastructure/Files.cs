// Adaptateur de fichiers : les prompts.
// La lecture stricte de l'UTF-8 (TextFiles) et du JSON (JsonText) vient de src/Shared. Le corpus,
// l'index, le client du service IA et la ligne de commande s'en servent aussi.

using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>
/// Prompts rangés dans des fichiers JSON versionnés avec le code, <c>prompts/&lt;nom&gt;.json</c>, qui
/// contiennent deux textes : system et user.
/// </summary>
public sealed class FilePromptRepository : IPromptRepository
{
    private readonly string _directory;

    public FilePromptRepository(string directory)
    {
        _directory = directory;
    }

    public PromptTemplate Get(string name)
    {
        var path = Path.Combine(_directory, name + ".json");
        try
        {
            var data = JsonText.Parse(TextFiles.ReadUtf8(path)) as JsonObject;
            if (JsonFiles.Text(data, "system") is not { } system || JsonFiles.Text(data, "user") is not { } user)
            {
                // Les prompts s'éditent à la main : le message doit dire quoi corriger, et dans quel fichier.
                throw new FormatException("il faut deux textes, system et user");
            }
            return new PromptTemplate(name, system.Trim(), user.Trim());
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // Fichier ou dossier absent : on donne le nom cherché, le dossier et les prompts disponibles, ce qui
            // aide plus que « Could not find file … ».
            throw new PromptNotFoundException(name, _directory, Names());
        }
        catch (Exception error) when (JsonFiles.IsMalformed(error))
        {
            // Le message nomme le fichier, puis ce qui ne va pas : la syntaxe, l'encodage ou un champ manquant.
            throw new FormatException($"prompt illisible ({path}) : {error.Message}", error);
        }
    }

    /// <summary>Les noms des prompts du dossier, triés.</summary>
    private IReadOnlyList<string> Names() =>
        !Directory.Exists(_directory)
            ? Array.Empty<string>()
            : Directory.GetFiles(_directory, "*.json").Select(p => Path.GetFileNameWithoutExtension(p))
                       .OrderBy(n => n, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Ce que lève la lecture d'un fichier JSON bien formé mais incomplet ou mal typé : une NullReferenceException
/// pour un champ absent, une InvalidOperationException ou une FormatException pour un type inattendu.
/// </summary>
internal static class JsonFiles
{
    public static bool IsMalformed(Exception error) =>
        error is JsonException or InvalidOperationException or NullReferenceException or FormatException
              or ArgumentException;

    /// <summary>Ici, une NullReferenceException signale un champ obligatoire absent. Son message n'aide pas.</summary>
    public static string Describe(Exception error) =>
        error is NullReferenceException ? "champ obligatoire manquant" : error.Message;

    /// <summary>Le champ texte <paramref name="key"/>, ou null s'il manque ou n'est pas un texte.</summary>
    public static string? Text(JsonObject? values, string key) =>
        values?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
