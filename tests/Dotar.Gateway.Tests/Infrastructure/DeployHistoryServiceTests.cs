using Dotar.Gateway.Infrastructure.Services;

namespace Dotar.Gateway.Tests.Infrastructure;

public class DeployHistoryServiceTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string BuildJson(string extraProps = "", string historyEntry = "")
    {
        var entry = historyEntry != string.Empty ? historyEntry : $$"""
            {
              "version": "1.0.0",
              "builtAt": "2024-01-15T10:00:00+00:00",
              "deployedBy": "tester",
              "host": "srv",
              "bumpType": "major",
              "gitCommit": "abc123",
              "changes": "Cambio inicial"
              {{(extraProps != string.Empty ? "," + extraProps : "")}}
            }
            """;

        return $$"""
            {
              "current": "1.0.0",
              "history": [ {{entry}} ]
            }
            """;
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVersionJson_ConNovedades_MapeaLista()
    {
        var json = BuildJson(extraProps: """
            "novedades": ["a", "b"]
            """);

        var (_, history) = DeployHistoryService.ParseVersionJson(json);

        Assert.Single(history);
        Assert.Equal(new[] { "a", "b" }, history[0].Novedades);
    }

    [Fact]
    public void ParseVersionJson_SinNovedades_ListaVacia()
    {
        var json = BuildJson();

        var (_, history) = DeployHistoryService.ParseVersionJson(json);

        Assert.Single(history);
        Assert.NotNull(history[0].Novedades);
        Assert.Empty(history[0].Novedades);
    }

    [Fact]
    public void ParseVersionJson_CamposExistentes_SeMapean()
    {
        var json = $$"""
            {
              "current": "2.3.1",
              "history": [
                {
                  "version": "2.3.1",
                  "builtAt": "2025-06-01T08:30:00-03:00",
                  "deployedBy": "ale",
                  "host": "lab-oficina",
                  "bumpType": "minor",
                  "gitCommit": "deadbeef1234",
                  "changes": "[FEAT] nueva funcionalidad"
                }
              ]
            }
            """;

        var (current, history) = DeployHistoryService.ParseVersionJson(json);

        Assert.Equal("2.3.1", current);
        Assert.Single(history);
        var e = history[0];
        Assert.Equal("2.3.1", e.Version);
        Assert.Equal("[FEAT] nueva funcionalidad", e.Changes);
        Assert.Equal("deadbeef1234", e.GitCommit);
        Assert.Empty(e.Novedades);
    }

    [Fact]
    public void ParseVersionJson_NovedadesConElementoNoString_LosIgnora()
    {
        // El JSON tiene un número y un null mezclados con el string válido.
        // System.Text.Json: los no-string se ignoran; solo "ok" pasa.
        var json = $$"""
            {
              "current": "1.0.0",
              "history": [
                {
                  "version": "1.0.0",
                  "builtAt": "2024-01-01T00:00:00+00:00",
                  "deployedBy": "x",
                  "host": "x",
                  "bumpType": "patch",
                  "gitCommit": "000",
                  "changes": "",
                  "novedades": ["ok", 123, null]
                }
              ]
            }
            """;

        var (_, history) = DeployHistoryService.ParseVersionJson(json);

        Assert.Single(history);
        Assert.Equal(new[] { "ok" }, history[0].Novedades);
    }
}
