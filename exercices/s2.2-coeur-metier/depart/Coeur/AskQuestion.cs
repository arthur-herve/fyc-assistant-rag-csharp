// Cas d'usage : répondre à une question à partir des documents accessibles.   — À ÉCRIRE
//
// Déroulé, du plus déterministe au moins déterministe :
// 1. règles métier sur la question (déterministe) ;
// 2. recherche des passages, filtrée par les droits d'accès (déterministe à
//    modèle d'embeddings fixé) ;
// 3. seuil de pertinence : sans passage pertinent, on n'appelle pas le modèle ;
// 4. génération (probabiliste), encadrée par deux vérifications déterministes :
//    la forme de la sortie (décorateur de validation) et les citations.

using System.Text;

namespace Coeur;

public sealed record AskSettings(
    int TopK = 4,
    // Attention : ce seuil n'a de sens que pour UN modèle d'embeddings et UN corpus donnés.
    double MinScore = 0.35,
    int MaxAttempts = 2,
    double Temperature = 0.2,
    int MaxTokens = 400,
    int? Seed = null,
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
                       IPromptRepository prompts, AskSettings? settings = null, AccessPolicy? accessPolicy = null)
    {
        _embedder = embedder;
        _index = index;
        _generator = generator;
        _prompts = prompts;
        _settings = settings ?? new AskSettings();
        _access = accessPolicy ?? new AccessPolicy();
    }

    public AskSettings Settings => _settings;

    /// <summary>
    /// Met en forme les passages pour le prompt : « [n] Titre » puis le texte (sans espaces
    /// autour), un bloc par passage, blocs séparés par une ligne vide. La numérotation
    /// commence à 1 : c'est elle que les citations désignent.
    /// </summary>
    public static string FormatPassages(IReadOnlyList<Passage> passages)
    {
        throw new NotImplementedException("AskQuestion.FormatPassages : à écrire (exercice S2.2)");
    }

    /// <summary>
    /// Déroulé, du plus déterministe au moins déterministe :
    ///  1. question vide (après Trim) → <see cref="EmptyQuestionException"/> ;
    ///  2. pas de manifeste d'index → <see cref="IndexNotBuiltException"/> ;
    ///  3. vecteur de la question (<see cref="IEmbedder.EmbedQuery"/>) ; si son modèle ou sa
    ///     dimension diffèrent du manifeste → <see cref="IndexModelMismatchException"/> ;
    ///  4. recherche des TopK passages, en ne gardant que ceux que l'utilisateur peut lire
    ///     (prédicat <see cref="AccessPolicy.CanRead"/>) : les droits sont filtrés AVANT le prompt ;
    ///  5. passages pertinents = score ≥ MinScore ; s'il n'y en a aucun, réponse
    ///     <see cref="AnswerStatus.NoRelevantSource"/> avec <see cref="Messages.NoRelevantSource"/>,
    ///     sans appeler le générateur (trace : 0 tentative, passages retrouvés et scores arrondis à 4 décimales) ;
    ///  6. prompt = <see cref="PromptTemplate.Render"/>(question, FormatPassages(pertinents)) ;
    ///  7. jusqu'à MaxAttempts tentatives (graine = Seed + tentative − 1 si Seed est fixée) :
    ///     appeler le générateur, garder la sortie brute dans la trace, vérifier les citations
    ///     (<see cref="Citations.Check"/>) ; si elles sont valides, réponse
    ///     <see cref="AnswerStatus.Answered"/> avec une <see cref="Source"/> par numéro cité ;
    ///     une <see cref="ModelOutputRejectedException"/> compte comme une tentative ratée
    ///     (trace « &lt;rejetée : problèmes&gt; texte ») ;
    ///  8. sinon, réponse <see cref="AnswerStatus.Unsourced"/> avec <see cref="Messages.Unsourced"/>
    ///     et tous les passages pertinents comme sources.
    /// La trace (<see cref="AnswerTrace"/>) porte l'identifiant de l'index, le modèle d'embeddings,
    /// le modèle de génération réellement utilisé, la version du prompt, les passages retrouvés,
    /// le seuil, le nombre de tentatives et les sorties brutes.
    /// Tests : Coeur.Tests/AskQuestionTests.cs.
    /// </summary>
    public Answer Execute(User user, string question)
    {
        throw new NotImplementedException("AskQuestion.Execute : à écrire (exercice S2.2)");
    }
}
