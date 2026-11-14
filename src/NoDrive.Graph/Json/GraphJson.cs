using System.Text.Json.Serialization;

namespace NoDrive.Graph.Json;

// Wire shapes of the Graph responses we read. Source-generated serializer: no reflection, trim/AOT safe.
internal sealed class DeltaResponse
{
    [JsonPropertyName("value")] public List<DriveItemDto> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
    [JsonPropertyName("@odata.deltaLink")] public string? DeltaLink { get; set; }
}

internal sealed class DriveItemDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("lastModifiedDateTime")] public DateTimeOffset? LastModified { get; set; }
    [JsonPropertyName("eTag")] public string? ETag { get; set; }
    [JsonPropertyName("cTag")] public string? CTag { get; set; }
    [JsonPropertyName("parentReference")] public ParentReferenceDto? Parent { get; set; }
    [JsonPropertyName("folder")] public object? Folder { get; set; }
    [JsonPropertyName("root")] public object? Root { get; set; }
    [JsonPropertyName("file")] public FileDto? File { get; set; }
    [JsonPropertyName("deleted")] public object? Deleted { get; set; }
    [JsonPropertyName("@microsoft.graph.downloadUrl")] public string? DownloadUrl { get; set; }
}

internal sealed class ParentReferenceDto { [JsonPropertyName("id")] public string? Id { get; set; } }
internal sealed class FileDto { [JsonPropertyName("hashes")] public HashesDto? Hashes { get; set; } }
internal sealed class HashesDto { [JsonPropertyName("quickXorHash")] public string? QuickXorHash { get; set; } }

internal sealed class UploadSessionDto { [JsonPropertyName("uploadUrl")] public string? UploadUrl { get; set; } }

internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

internal sealed class GraphError
{
    [JsonPropertyName("error")] public GraphErrorBody? Error { get; set; }
}

internal sealed class GraphErrorBody
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeltaResponse))]
[JsonSerializable(typeof(DriveItemDto))]
[JsonSerializable(typeof(UploadSessionDto))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(GraphError))]
internal sealed partial class GraphJsonContext : JsonSerializerContext { }
