// Lecture stricte du JSON. L'écriture, elle, reste celle de System.Text.Json.
// Ce fichier source est partagé : Assistant.Infrastructure et Assistant.Cli le compilent chacun (voir leurs .csproj)
// et en ont leur propre copie interne. Ainsi, la ligne de commande n'a pas à nommer l'infrastructure, que seule la
// racine de composition connaît (InfrastructureLeakTests).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant;

/// <summary>
/// Lecture stricte (<see cref="Parse"/>, <see cref="ParseDocument"/>). Tel quel, System.Text.Json garderait sans prévenir
/// l'une des valeurs d'une clé en double, et signalerait en anglais un document de plus de 64 niveaux d'imbrication. Ici, ces
/// deux cas sont refusés avec un message en français. Une erreur de syntaxe garde le message de System.Text.Json, y compris
/// pour NaN et Infinity, qui ne sont pas du JSON.
/// L'appelant peut demander deux refus de plus. Avec <c>strings</c>, une chaîne qui n'est pas du texte est refusée :
/// la configuration et l'index le demandent. Avec <c>maxDigits</c>, c'est un
/// entier de plus de 4300 chiffres, et seule la configuration le demande.
/// </summary>
internal static class JsonText
{
    /// <summary>Niveaux d'imbrication permis : la limite par défaut de System.Text.Json.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Nombre maximal de chiffres d'un entier, quand l'appelant le demande avec <c>maxDigits</c>.
    /// </summary>
    public const int MaxDigits = 4300;

    /// <summary>
    /// Au-delà de 900 niveaux d'imbrication, le texte est refusé d'emblée comme « trop imbriqué », avant toute lecture et
    /// quel que soit le reste de son contenu (<see cref="CheckReadDepth"/>). Le lecteur de System.Text.Json n'est pas
    /// récursif, et la limite de 64 niveaux l'arrête bien avant. Cette pré-lecture ne change donc que l'erreur signalée,
    /// pour un texte de plus de 900 niveaux qui contient un autre défaut plus tôt.
    /// </summary>
    private const int ReadDepth = 900;

    private const string NotText = "chaîne qui n'est pas du texte : surrogate UTF-16 isolé (\\ud800 à \\udfff sans sa paire)";
    private static readonly string TooDeep = $"JSON trop imbriqué : plus de {MaxDepth} niveaux";

    /// <summary>Le JSON de <paramref name="text"/>, vérifié par <see cref="Check"/>.</summary>
    public static JsonNode? Parse(string text, bool strings = false, int? maxDigits = null)
    {
        Check(text, strings, maxDigits);
        return JsonNode.Parse(text);
    }

    /// <summary>Le JSON de <paramref name="text"/>, vérifié par <see cref="Check"/>.</summary>
    public static JsonDocument ParseDocument(string text, bool strings = false, int? maxDigits = null)
    {
        Check(text, strings, maxDigits);
        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// Lève <see cref="FormatException"/> au premier défaut rencontré. Un texte de plus de 900 niveaux est refusé en bloc,
    /// avant toute lecture (<see cref="ReadDepth"/>). La lecture refuse ensuite une clé en double et un objet ou une liste
    /// au-delà de 64 niveaux. Avec <paramref name="strings"/>, elle refuse aussi une chaîne ou une clé qui contient une
    /// demi-paire de substitution UTF-16 isolée, et avec <paramref name="maxDigits"/>, un entier plus long que la limite.
    /// Une syntaxe invalide, y compris NaN et Infinity, lève <see cref="JsonException"/>, comme <c>JsonNode.Parse</c>.
    /// </summary>
    public static void Check(string text, bool strings = false, int? maxDigits = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        CheckReadDepth(bytes);
        // Le lecteur reçoit un niveau de plus que MaxDepth. Il laisse ainsi passer l'ouverture du 65e niveau, que le
        // contrôle ci-dessous refuse avec un message en français, là où le lecteur l'aurait refusée en anglais.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxDepth + 1 });
        var containers = new Stack<HashSet<string>?>();   // les clés de chaque objet ouvert, ou null pour une liste
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    if (reader.CurrentDepth >= MaxDepth)
                    {
                        throw new FormatException(TooDeep);
                    }
                    containers.Push(reader.TokenType == JsonTokenType.StartObject ? new HashSet<string>(StringComparer.Ordinal) : null);
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (Text(ref reader) is { } key)
                    {
                        if (!containers.Peek()!.Add(key))
                        {
                            throw new FormatException($"clé « {key} » en double");
                        }
                    }
                    else if (strings)
                    {
                        throw new FormatException(NotText);
                    }
                    break;
                case JsonTokenType.String when strings:
                    if (Text(ref reader) is null)
                    {
                        throw new FormatException(NotText);
                    }
                    break;
                case JsonTokenType.Number when maxDigits is { } digits && IsLongInteger(reader.ValueSpan, digits):
                    throw new FormatException($"nombre entier de plus de {digits} chiffres");
            }
        }
    }

    /// <summary>
    /// Refuse d'emblée un texte de plus de 900 niveaux d'imbrication, même si un autre défaut vient plus tôt dans le texte.
    /// On compte les crochets et les accolades hors des chaînes, sans analyser la syntaxe. Dans une chaîne, un antislash
    /// protège l'octet qui le suit, et une chaîne non fermée court jusqu'à la fin du texte.
    /// </summary>
    private static void CheckReadDepth(ReadOnlySpan<byte> bytes)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < bytes.Length; i++)
        {
            switch (bytes[i])
            {
                case (byte)'\\' when inString:
                    i++;
                    break;
                case (byte)'"':
                    inString = !inString;
                    break;
                case (byte)'[' or (byte)'{' when !inString:
                    if (++depth > ReadDepth)
                    {
                        throw new FormatException(TooDeep);
                    }
                    break;
                case (byte)']' or (byte)'}' when !inString:
                    depth--;
                    break;
            }
        }
    }

    /// <summary>
    /// Le texte du jeton, clé ou chaîne, ou null s'il contient une demi-paire de substitution isolée, qui n'est pas du
    /// texte. Sans <c>strings</c>, ce défaut passe ici, et c'est la lecture qui suit qui le refusera au premier accès.
    /// </summary>
    private static string? Text(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Un entier sans décimales ni exposant, de plus de <paramref name="digits"/> chiffres hors signe.</summary>
    private static bool IsLongInteger(ReadOnlySpan<byte> number, int digits) =>
        number.IndexOfAny("eE."u8) < 0 && number.Length - (number[0] == '-' ? 1 : 0) > digits;
}
