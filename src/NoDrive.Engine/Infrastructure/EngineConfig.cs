using System.Text.Json;
using System.Text.Json.Serialization;
using NoDrive.Core.Population;
using NoDrive.Graph;

namespace NoDrive.Engine.Infrastructure;

/// <summary>User-editable settings (%LOCALAPPDATA%\NoDrive\config.json). Missing file or fields fall back to the defaults below.</summary>
internal sealed class EngineConfig
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = GraphEndpoints.DefaultClientId;
    [JsonPropertyName("syncRoot")] public string? SyncRoot { get; set; }
    [JsonPropertyName("hydrationWorkers")] public int HydrationWorkers { get; set; } = 3;
    [JsonPropertyName("populationBudget")] public long PopulationBudget { get; set; } = 150_000;
    [JsonPropertyName("pollActiveSeconds")] public int PollActiveSeconds { get; set; } = 60;
    [JsonPropertyName("pollIdleMaxSeconds")] public int PollIdleMaxSeconds { get; set; } = 900;

    public PopulationOptions ToPopulationOptions() => new() { PlaceholderBudget = PopulationBudget };

    public static EngineConfig Load(string path)
    {
        try
        {
            if (File.Exists(path)) { return JsonSerializer.Deserialize(File.ReadAllText(path), EngineJson.Default.EngineConfig) ?? new EngineConfig(); }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { Log.Warn($"config.json ignored: {ex.Message}"); }
        return new EngineConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, EngineJson.Default.EngineConfig));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EngineConfig))]
internal sealed partial class EngineJson : JsonSerializerContext { }
