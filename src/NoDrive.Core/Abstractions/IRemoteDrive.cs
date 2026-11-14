using NoDrive.Core.Model;

namespace NoDrive.Core.Abstractions;

/// <summary>The remote side of the sync (OneDrive through Microsoft Graph in production, a fake in tests).</summary>
public interface IRemoteDrive
{
    /// <summary>
    /// Streams delta pages. <paramref name="resumeLink"/> is a nextLink (resume an interrupted enumeration), a deltaLink (incremental round)
    /// or null (full enumeration). Throws <see cref="ResyncRequiredException"/> when the server says the token is no longer valid (HTTP 410).
    /// </summary>
    IAsyncEnumerable<DeltaPage> GetDeltaAsync(string? resumeLink, CancellationToken cancellationToken);
}

/// <summary>The delta token expired (Graph 410 Gone): the caller must re-enumerate the whole drive from scratch.</summary>
public sealed class ResyncRequiredException : Exception
{
    public ResyncRequiredException() : base("The delta token is no longer valid; a full resync is required.") { }
    public ResyncRequiredException(string message) : base(message) { }
    public ResyncRequiredException(string message, Exception inner) : base(message, inner) { }
}
