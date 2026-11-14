using System.Text.Json.Serialization;

namespace NoDrive.Spikes.Scale;

// Subset of the Graph driveItem used by the engine; source-generated serializer = AOT/trim safe, no reflection.
internal sealed class DeltaPage
{
    [JsonPropertyName("value")] public List<DriveItem> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
    [JsonPropertyName("@odata.deltaLink")] public string? DeltaLink { get; set; }
}

internal sealed class DriveItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("lastModifiedDateTime")] public string? LastModified { get; set; }
    [JsonPropertyName("createdDateTime")] public string? Created { get; set; }
    [JsonPropertyName("eTag")] public string? ETag { get; set; }
    [JsonPropertyName("cTag")] public string? CTag { get; set; }
    [JsonPropertyName("parentReference")] public ParentRef? Parent { get; set; }
    [JsonPropertyName("folder")] public FolderFacet? Folder { get; set; }
    [JsonPropertyName("file")] public FileFacet? File { get; set; }
    [JsonPropertyName("deleted")] public DeletedFacet? Deleted { get; set; }
}

internal sealed class ParentRef
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("driveId")] public string? DriveId { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
}

internal sealed class FolderFacet { [JsonPropertyName("childCount")] public int ChildCount { get; set; } }
internal sealed class DeletedFacet { [JsonPropertyName("state")] public string? State { get; set; } }
internal sealed class FileFacet
{
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
    [JsonPropertyName("hashes")] public Hashes? Hashes { get; set; }
}
internal sealed class Hashes
{
    [JsonPropertyName("quickXorHash")] public string? QuickXorHash { get; set; }
    [JsonPropertyName("sha1Hash")] public string? Sha1Hash { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(DeltaPage))]
internal sealed partial class ScaleJsonContext : JsonSerializerContext { }
