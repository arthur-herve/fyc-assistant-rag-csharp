// Séquence 3.1 : tester un composant probabiliste.
//
// Un test par assertion exacte sur la sortie d'un modèle échouerait au hasard. On teste
// donc une PROPORTION avec une tolérance, et on documente la probabilité de faux échec.
//
// Le « modèle » ci-dessous oublie de citer ses sources 3 fois sur 10 (p = 0,3). Avec deux
// tentatives, une question reçoit une réponse sourcée avec la probabilité 1 − p² = 0,91.
// Sur 50 questions, le nombre de réponses sourcées suit une loi binomiale B(50 ; 0,91) :
// P(taux < 0,75) = P(X ≤ 37) ≈ 0,0004, soit moins d'un faux échec pour 2 000 exécutions.
// À 0,80 (X ≤ 39) le risque monte à ≈ 0,4 % ; à 0,85 il serait de ≈ 8 % : une tolérance
// trop serrée fabrique des tests instables. (Calcul : exercices/s3.1-evaluation-statistique.)
//
// Ce que ce test NE teste PAS : que le modèle est bon. Il teste que l'application tient
// sa promesse malgré un modèle imparfait — et la règle métier, elle, reste déterministe :
// jamais une réponse non sourcée n'est affichée comme une réponse.

using Assistant.Application;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public sealed class ForgetfulGenerator : IGenerator
{
    public const double ForgetRate = 0.3;
    private readonly Random _random = new();   // volontairement non initialisé : le hasard est le sujet

    public List<GenerationRequest> Requests { get; } = new();

    public Generation Generate(GenerationRequest request)
    {
        Requests.Add(request);
        var cited = _random.NextDouble() >= ForgetRate;
        return new Generation("forgetful", cited ? "Deux jours [1]." : "Deux jours.");
    }
}

public class StatisticalTests
{
    private const int Trials = 50;
    private const double MinAnswerRate = 0.75;   // attendu ≈ 0,91 avec 2 tentatives ; faux échec < 0,1 %

    [Fact]
    public void Answer_rate_stays_above_the_tolerance()
    {
        var index = Build.Indexed();
        var ask = Build.Ask(new ForgetfulGenerator(), index: index, maxAttempts: 2);
        var statuses = Enumerable.Range(0, Trials).Select(_ => ask.Execute(Fakes.Alice, "jours de télétravail").Status).ToList();

        var rate = (double)statuses.Count(s => s == AnswerStatus.Answered) / Trials;
        Assert.True(rate >= MinAnswerRate, $"taux de réponse sourcée : {rate:0.00}");

        // La règle métier, elle, est déterministe : jamais de réponse non sourcée affichée.
        Assert.All(statuses, s => Assert.True(s is AnswerStatus.Answered or AnswerStatus.Unsourced));
    }

    [Fact]
    public void The_false_failure_probability_is_documented_and_small()
    {
        // Le calcul du commentaire d'en-tête, vérifié par le test lui-même.
        var p = 1 - ForgetfulGenerator.ForgetRate * ForgetfulGenerator.ForgetRate;   // 0,91
        var falseFailure = BinomialCdf(Trials, p, (int)Math.Ceiling(MinAnswerRate * Trials) - 1);
        Assert.InRange(falseFailure, 0, 0.001);
        Assert.InRange(BinomialCdf(Trials, p, 39), 0.001, 0.01);   // à 0,80 : ≈ 0,4 %
        Assert.InRange(BinomialCdf(Trials, p, 42), 0.05, 0.10);    // à 0,85 : ≈ 8 %, trop fragile
    }

    /// <summary>P(X ≤ k) pour X ~ B(n, p).</summary>
    public static double BinomialCdf(int n, double p, int k)
    {
        var total = 0.0;
        for (var i = 0; i <= k; i++)
        {
            total += Binomial(n, i) * Math.Pow(p, i) * Math.Pow(1 - p, n - i);
        }
        return total;
    }

    private static double Binomial(int n, int k)
    {
        var result = 1.0;
        for (var i = 1; i <= k; i++)
        {
            result = result * (n - k + i) / i;
        }
        return result;
    }
}
