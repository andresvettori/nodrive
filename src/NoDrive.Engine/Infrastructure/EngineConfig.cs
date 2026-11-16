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
    [JsonPropertyName("hydrationWorkers")] public int HydrationWorkers { get; set; } = 4;
    [JsonPropertyName("populationBudget")] public long PopulationBudget { get; set; } = 150_000;
    [JsonPropertyName("pollActiveSeconds")] public int PollActiveSeconds { get; set; } = 60;
    [JsonPropertyName("pollIdleMaxSeconds")] public int PollIdleMaxSeconds { get; set; } = 900;
    /// <summary>Screen language: "auto" (follow Windows), "en" or "es".</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    /// <summary>Download and upload limits in KiB/s; 0 means unlimited. Applied live.</summary>
    [JsonPropertyName("downloadKiBps")] public int DownloadKiBps { get; set; }
    [JsonPropertyName("uploadKiBps")] public int UploadKiBps { get; set; }
    /// <summary>"full" (whole file on first access, like OneDrive; default) or "progressive". Applied at the next start.</summary>
    [JsonPropertyName("hydrationPolicy")] public string HydrationPolicy { get; set; } = "full";
    /// <summary>Port of the local settings page; 0 picks the default (47653) or a free one.</summary>
    [JsonPropertyName("webPort")] public int WebPort { get; set; }

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
