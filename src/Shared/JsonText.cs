// JSON lu strictement ; l'écriture, elle, est celle de System.Text.Json. Fichier source partagé : compilé dans
// Assistant.Infrastructure et dans Assistant.Cli (voir leurs .csproj), qui en ont chacune leur copie interne ; la
// ligne de commande ne dépend donc pas de l'infrastructure (ArchitectureTests).

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant;

/// <summary>
/// Lecture (<see cref="Parse"/>, <see cref="ParseDocument"/>) : System.Text.Json garderait sans rien dire l'une des valeurs
/// d'une clé en double, et dirait en anglais qu'un document a plus de 64 niveaux d'imbrication ; ces refus ont ici un message
/// en français. Une erreur de syntaxe, NaN et Infinity compris (ce n'est pas du JSON), garde le message de System.Text.Json.
/// Sur demande : une chaîne qui n'est pas du texte (<c>strings</c> : la configuration, l'index, les instantanés, les jeux de
/// questions, l'API HTTP et GET /v1/models), et un entier de plus de 4300 chiffres (<c>maxDigits</c> : la configuration et
/// GET /v1/models).
/// </summary>
internal static class JsonText
{
    /// <summary>Niveaux d'imbrication permis : la limite par défaut de System.Text.Json.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Chiffres d'un entier, au plus, quand <c>maxDigits</c> le demande : la configuration et GET /v1/models.
    /// </summary>
    public const int MaxDigits = 4300;

    /// <summary>
    /// Au-delà de 900 niveaux d'imbrication, le texte est refusé d'emblée (« trop imbriqué »), avant toute lecture, quoi qu'il
    /// contienne d'autre (<see cref="CheckReadDepth"/>). Le lecteur de System.Text.Json n'est pas récursif, et la limite de
    /// 64 niveaux l'arrête bien avant : la pré-lecture ne change que le défaut signalé pour un texte de plus de 900 niveaux
    /// qui en a un autre avant.
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
    /// Refuse (<see cref="FormatException"/>) au premier défaut rencontré : au-delà de 900 niveaux, le texte entier, avant
    /// toute lecture (<see cref="ReadDepth"/>) ; puis, en lisant, une clé en double, un objet ou une liste au-delà de
    /// 64 niveaux, avec <paramref name="strings"/>, une chaîne (clé comprise) qui n'est pas du texte (demi-paire de
    /// substitution UTF-16 isolée) et, avec <paramref name="maxDigits"/>, un entier plus long. Une syntaxe invalide, NaN et
    /// Infinity compris, lève <see cref="JsonException"/>, comme <c>JsonNode.Parse</c>.
    /// </summary>
    public static void Check(string text, bool strings = false, int? maxDigits = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        CheckReadDepth(bytes);
        // Un niveau de plus que MaxDepth : le lecteur laisse passer l'ouverture du 65e, que le contrôle ci-dessous refuse
        // en français (le lecteur la refuserait en anglais).
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxDepth + 1 });
        var containers = new Stack<HashSet<string>?>();   // les clés de chaque objet ouvert ; null : une liste
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
    /// Plus de 900 niveaux d'imbrication : refusé d'emblée, avant la lecture, même si un autre défaut vient avant dans le
    /// texte. Les crochets et les accolades sont comptés hors des chaînes (un antislash y garde l'octet qui le suit ; une
    /// chaîne non fermée court jusqu'à la fin), sans lire la syntaxe.
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
    /// Le texte du jeton (clé ou chaîne) ; null pour une demi-paire de substitution isolée, qui n'en est pas. Sans
    /// <c>strings</c>, la lecture qui suit la refusera au premier accès.
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

    /// <summary>Un entier (ni virgule ni exposant) de plus de <paramref name="digits"/> chiffres, signe non compté.</summary>
    private static bool IsLongInteger(ReadOnlySpan<byte> number, int digits) =>
        number.IndexOfAny("eE."u8) < 0 && number.Length - (number[0] == '-' ? 1 : 0) > digits;
}
