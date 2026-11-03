// Cas d'usage : répondre à une question à partir des documents accessibles.
//
// Les étapes vont du plus déterministe au moins déterministe :
// 1. Les règles métier sur la question.
// 2. La recherche des passages, filtrée par les droits d'accès. Elle reste déterministe tant que
//    le modèle d'embeddings ne change pas.
// 3. Le seuil de pertinence. Sans passage pertinent, on n'appelle pas le modèle.
// 4. La génération, seule étape probabiliste. Une vérification déterministe la suit : les citations.

using Assistant.Domain;

namespace Assistant.Application;

public sealed record AskSettings(
    double MinScore,
    int TopK = 4,
    double Temperature = 0.2,
    int MaxTokens = 400,
    string PromptName = "answer");

public sealed class AskQuestion
{
    private readonly IEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly IGenerator _generator;
    private readonly IPromptRepository _prompts;
    private readonly AskSettings _settings;
    private readonly AccessPolicy _access;

    public AskQuestion(IEmbedder embedder, IVectorIndex index, IGenerator generator,
                       IPromptRepository prompts, AskSettings settings)
    {
        _embedder = embedder;
        _index = index;
        _generator = generator;
        _prompts = prompts;
        _settings = settings;
        _access = new AccessPolicy();
    }

    /// <summary>
    /// Numérote les passages pour que le modèle puisse les citer. Quand le découpage a déjà mis le titre dans
    /// le morceau, il apparaît deux fois. On garde cette redondance de quelques mots : la retirer changerait les
    /// prompts.
    ///
    /// Le format à écrire : chaque passage donne un bloc, qui commence par une ligne « [n] Titre du document »,
    /// suivie du texte du morceau, sans les blancs qui l'entourent. Une ligne vide sépare deux blocs. La
    /// numérotation commence à 1, car ce sont ces numéros que les citations désignent.
    /// </summary>
    public static string FormatPassages(IReadOnlyList<Passage> passages)
    {
        throw new NotImplementedException("AskQuestion.FormatPassages : à écrire (exercice S2.2)");
    }

    /// <summary>
    /// Le déroulé à écrire, étape par étape :
    ///  1. On retire les blancs qui entourent la question : c'est cette question rognée qui sert ensuite. Si elle
    ///     est vide, on lève <see cref="EmptyQuestionException"/>.
    ///  2. Si l'index n'est pas construit, on lève <see cref="IndexNotBuiltException"/>.
    ///  3. On calcule le vecteur de la question avec <see cref="IEmbedder.EmbedQuery"/>, puis on cherche les TopK
    ///     passages les plus proches, en ne gardant que ceux que l'utilisateur a le droit de lire, avec
    ///     <see cref="AccessPolicy.CanRead"/> comme prédicat. Les droits sont donc filtrés avant le prompt.
    ///  4. Un passage est pertinent si son score est supérieur ou égal à MinScore. S'il n'y en a aucun, on rend une
    ///     réponse <see cref="AnswerStatus.NoRelevantSource"/> avec le texte <see cref="Messages.NoRelevantSource"/>,
    ///     sans appeler le générateur.
    ///  5. Sinon, on prend le modèle de prompt nommé PromptName, et on construit le prompt avec
    ///     <see cref="PromptTemplate.Render"/>, à partir de la question et des passages pertinents mis en forme par
    ///     FormatPassages. On appelle alors le générateur avec les consignes du modèle de prompt, le prompt construit,
    ///     la température et le nombre maximal de jetons.
    ///  6. On vérifie les citations de sa sortie avec <see cref="Citations.Check"/>. Si elles sont valides, on rend
    ///     une réponse <see cref="AnswerStatus.Answered"/>, avec le texte du modèle sans les blancs qui l'entourent,
    ///     et une <see cref="Source"/> par numéro cité. Sinon, on rend une réponse <see cref="AnswerStatus.Unsourced"/>
    ///     avec le texte <see cref="Messages.Unsourced"/>, et tous les passages pertinents comme sources.
    ///
    /// Dans tous les cas, la trace (<see cref="AnswerTrace"/>) garde les passages retrouvés, même sous le seuil,
    /// avec leur score arrondi à 4 décimales, le seuil appliqué et les sorties brutes du modèle.
    /// </summary>
    public Answer Execute(User user, string question)
    {
        throw new NotImplementedException("AskQuestion.Execute : à écrire (exercice S2.2)");
    }
}
