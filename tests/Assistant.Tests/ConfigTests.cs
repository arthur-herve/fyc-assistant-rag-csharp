// Configuration et ligne de commande : une erreur de saisie se voit tout de suite, avec le fichier et la clé.

using Assistant.Application;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

public sealed class ConfigTests : IDisposable
{
    private const string Valid = """
        {
          "ai_service": { "base_url": "http://127.0.0.1:8100", "embedding_model": "hashing", "generation_model": "extractive" },
          "corpus": { "directory": "corpus/solveo" },
          "index": { "path": "data/index.json" },
          "retrieval": { "min_score": { "default": 0.4, "hashing": 0.15 } },
          "decorators": { "validate_output": true }
        }
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("fyc-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppConfig Load(string json)
    {
        var path = Path.Combine(_dir, "app.json");
        File.WriteAllText(path, json);
        return AppConfig.Load(path);
    }

    [Theory]
    [InlineData("app.json")]
    [InlineData("app-ollama.json")]
    [InlineData("app-ollama-complet.json")]
    public void The_shipped_configurations_load(string name) =>
        Assert.NotEmpty(AppConfig.Load(Path.Combine(AppConfig.ProjectRoot, "config", name)).Users);

    [Theory]
    [InlineData("embedding_model", "\"embedding_model\": \"hashing\", ", "")]
    [InlineData("validate_ouput", "\"validate_output\": true", "\"validate_ouput\": true")]
    [InlineData("validate_output", "\"validate_output\": true", "\"validate_output\": \"oui\"")]
    [InlineData("max_attempts", "\"decorators\"", "\"generation\": { \"max_attempts\": 0 }, \"decorators\"")]
    [InlineData("top_k", "\"min_score\"", "\"top_k\": 0, \"min_score\"")]
    [InlineData("topk", "\"min_score\"", "\"topk\": 8, \"min_score\"")]
    [InlineData("max_char", "\"decorators\"", "\"splitter\": { \"max_char\": 300 }, \"decorators\"")]
    [InlineData("overlap_chars", "\"decorators\"", "\"splitter\": { \"overlap_chars\": \"120\" }, \"decorators\"")]
    [InlineData("timeout_seconds", "\"generation_model\": \"extractive\"", "\"generation_model\": \"extractive\", \"timeout_seconds\": 0")]
    [InlineData("retreival", "\"decorators\"", "\"retreival\": { \"top_k\": 8 }, \"decorators\"")]
    public void Errors_name_the_key(string key, string from, string to)
    {
        var error = Assert.Throws<ConfigException>(() => Load(Valid.Replace(from, to)));
        Assert.Contains(key, error.Message);
        Assert.Contains("app.json", error.Message);
    }

    [Fact]
    public void Threshold_per_model_and_uncalibrated_fallback()
    {
        var config = Load(Valid);
        Assert.Equal(0.15, config.MinScoreFor("hashing"));
        Assert.True(config.HasThresholdFor("hashing"));
        Assert.Equal(0.4, config.MinScoreFor("nomic"));        // repli sur « default »…
        Assert.False(config.HasThresholdFor("nomic"));         // … signalé (ligne de commande, banc)
    }

    [Fact]
    public void Help_is_a_success_and_a_mistyped_option_an_error()
    {
        Assert.Equal(0, Program.Main(new[] { "--help" }));
        Assert.Equal(1, Program.Main(Array.Empty<string>()));
        // « --usr bruno » ne répond pas au nom d'alice : l'option inconnue est refusée avant tout travail
        // (le code 1 seul ne le prouve pas : sans index, « ask » échouerait de toute façon).
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            Assert.Equal(1, Program.Main(new[] { "ask", "Combien de jours ?", "--usr", "bruno" }));
        }
        finally
        {
            Console.SetError(previous);
        }
        Assert.Contains("option(s) inconnue(s) pour ask : --usr", stderr.ToString());
    }

    [Fact]
    public void The_snapshot_configuration_includes_the_settings_that_change_statuses()
    {
        var config = AppConfig.Load() with { AiBaseUrl = "http://127.0.0.1:1" };
        var container = Composition.Build(config, new Overrides(IndexPath: Path.Combine(_dir, "index.json"),
                                                                SnapshotsDir: Path.Combine(_dir, "instantanes")));
        var snapshot = container.RecordSnapshot.Execute("vide", Array.Empty<SnapshotQuestion>());
        Assert.Equal((object?)2, snapshot.Configuration["max_attempts"]);
        Assert.Equal((object?)true, snapshot.Configuration["validate_output"]);
        Assert.Equal((object?)1500, snapshot.Configuration["max_output_chars"]);
    }
}
