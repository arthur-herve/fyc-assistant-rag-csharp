// Séquence 3.1 : tester un composant probabiliste.
//
// Une assertion exacte sur la sortie d'un modèle ferait échouer le test au hasard. On teste
// donc une proportion, avec une tolérance, et on documente la probabilité de faux échec.
//
// Le « modèle » ci-dessous oublie de citer ses sources 3 fois sur 10 (p = 0,3). Avec une seule
// tentative, une question reçoit une réponse sourcée avec la probabilité 1 − p = 0,70. Sur 50
// questions, le nombre de réponses sourcées suit donc une loi binomiale B(50 ; 0,70). Avec une
// tolérance de 0,50, P(taux < 0,50) = P(X ≤ 24) ≈ 0,0009, soit un peu moins d'un faux échec pour
// 1 000 exécutions.
//
// Avec deux tentatives, la probabilité passe à 1 − p² = 0,91, et la loi à B(50 ; 0,91). Avec une
// tolérance de 0,75, P(taux < 0,75) = P(X ≤ 37) ≈ 0,0004, soit moins d'un faux échec pour 2 000
// exécutions.
// À 0,80 (X ≤ 39), le risque monte à environ 0,4 %, et à 0,85 il serait d'environ 8 % : une
// tolérance trop serrée fabrique des tests instables. La tolérance dépend aussi du mécanisme
// testé : avec une seule tentative, celle de 0,75 ferait échouer le test environ 78 fois sur 100.
// Le calcul est détaillé dans exercices/s3.1-evaluation-statistique.
//
// Ce test ne vérifie pas que le modèle est bon. Il vérifie que l'application tient sa
// promesse malgré un modèle imparfait. La règle métier, elle, reste déterministe : une
// réponse non sourcée n'est jamais affichée comme une réponse.

using Assistant.Application;
using Assistant.Domain;
using Xunit;

namespace Assistant.Tests;

public sealed class ForgetfulGenerator : IGenerator
{
    public const double ForgetRate = 0.3;
    private readonly Random _random = new();   // sans graine, exprès : le hasard est justement le sujet

    public List<GenerationRequest> Requests { get; } = new();

    public string Generate(GenerationRequest request)
    {
        Requests.Add(request);
        var cited = _random.NextDouble() >= ForgetRate;
        return cited ? "Deux jours [1]." : "Deux jours.";
    }
}

public class StatisticalTests
{
    private const int Trials = 50;
    private const double MinAnswerRateWithOneAttempt = 0.50;   // on attend 0,70 avec 1 tentative, et 0,093 % de faux échecs
    private const double MinAnswerRate = 0.75;                 // on attend ≈ 0,91 avec 2 tentatives, et moins de 0,1 % de faux échecs

    [Fact]
    public void Answer_rate_with_a_single_attempt_stays_above_its_tolerance()
    {
        var rate = AnswerRate(maxAttempts: 1);
        Assert.True(rate >= MinAnswerRateWithOneAttempt, $"taux de réponse sourcée : {rate:0.00}");
    }

    [Fact]
    public void Answer_rate_stays_above_the_tolerance()
    {
        var rate = AnswerRate(maxAttempts: 2);
        Assert.True(rate >= MinAnswerRate, $"taux de réponse sourcée : {rate:0.00}");
    }

    /// <summary>Pose <see cref="Trials"/> fois la même question et rend le taux de réponses sourcées.</summary>
    private static double AnswerRate(int maxAttempts)
    {
        var index = Build.Indexed();
        var ask = Build.Ask(new ForgetfulGenerator(), index: index, maxAttempts: maxAttempts);
        var answers = Enumerable.Range(0, Trials).Select(_ => ask.Execute(Fakes.Alice, "jours de télétravail")).ToList();

        return (double)answers.Count(a => a.Status == AnswerStatus.Answered) / Trials;
    }

    [Fact]
    public void The_false_failure_probability_is_documented_and_small()
    {
        // Le test vérifie lui-même les calculs du commentaire d'en-tête, pour une et pour deux tentatives.
        var once = 1 - ForgetfulGenerator.ForgetRate;   // 0,70
        Assert.InRange(BinomialCdf(Trials, once, FailsAtOrBelow(MinAnswerRateWithOneAttempt)), 0.0009, 0.001);   // ≈ 0,093 %
        Assert.InRange(BinomialCdf(Trials, once, FailsAtOrBelow(MinAnswerRate)), 0.75, 0.80);   // à 0,75 : ≈ 78 % d'échecs

        var twice = 1 - ForgetfulGenerator.ForgetRate * ForgetfulGenerator.ForgetRate;   // 0,91
        var falseFailure = BinomialCdf(Trials, twice, FailsAtOrBelow(MinAnswerRate));
        Assert.InRange(falseFailure, 0, 0.001);
        Assert.InRange(BinomialCdf(Trials, twice, 39), 0.001, 0.01);   // à 0,80 : ≈ 0,4 %
        Assert.InRange(BinomialCdf(Trials, twice, 42), 0.05, 0.10);    // à 0,85 : ≈ 8 %, trop fragile
    }

    /// <summary>Le plus grand nombre de réponses sourcées qui fait encore échouer le test, pour une tolérance donnée.</summary>
    private static int FailsAtOrBelow(double tolerance) => (int)Math.Ceiling(tolerance * Trials) - 1;

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
