using System.Runtime.CompilerServices;
using NoDrive.Core.Abstractions;
using NoDrive.Core.Index;
using NoDrive.Core.Model;

namespace NoDrive.Core.Tests;

internal sealed class TempIndex : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nodrive-tests-" + Guid.NewGuid().ToString("N"));
    public ItemIndex Index { get; }
    public string DbPath { get; }

    public TempIndex()
    {
        Directory.CreateDirectory(_dir);
        DbPath = Path.Combine(_dir, "index.db");
        Index = new ItemIndex(DbPath);
    }

    public void Dispose()
    {
        Index.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* best effort */ }
    }
}

internal static class Items
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public static RemoteItem Folder(string id, string? parent, string name) => new(id, parent, name, 0, T0, true, "e" + id, "c" + id, null);
    public static RemoteItem File(string id, string parent, string name, long size = 10) => new(id, parent, name, size, T0, false, "e" + id, "c" + id, "qx" + id);
}

/// <summary>A scripted remote: a list of pages served in order; a token "next:k" resumes at page k; "delta:n" serves the n-th incremental round.</summary>
internal sealed class ScriptedDrive : IRemoteDrive
{
    public List<DeltaPage> FullPages { get; } = [];
    public Dictionary<string, List<DeltaPage>> Incremental { get; } = [];
    public HashSet<string> ExpiredTokens { get; } = [];
    public int FailAfterPages { get; set; } = int.MaxValue;
    public List<string?> Requests { get; } = [];

    public static DeltaPage Page(int k, int total, params DeltaChange[] changes) =>
        new(changes, k < total - 1 ? $"next:{k + 1}" : null, k == total - 1 ? "delta:1" : null);

    public async IAsyncEnumerable<DeltaPage> GetDeltaAsync(string? resumeLink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(resumeLink);
        if (resumeLink is not null && ExpiredTokens.Contains(resumeLink)) { throw new ResyncRequiredException(); }

        IReadOnlyList<DeltaPage> pages;
        if (resumeLink is not null && resumeLink.StartsWith("delta:", StringComparison.Ordinal)) { pages = Incremental[resumeLink]; }
        else
        {
            int start = resumeLink is not null && resumeLink.StartsWith("next:", StringComparison.Ordinal) ? int.Parse(resumeLink[5..], System.Globalization.CultureInfo.InvariantCulture) : 0;
            pages = FullPages.Skip(start).ToList();
        }

        int served = 0;
        foreach (var page in pages)
        {
            if (served++ >= FailAfterPages) { throw new IOException("connection lost"); }
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return page;
        }
    }
}
