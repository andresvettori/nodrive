using Microsoft.Data.Sqlite;
using NoDrive.Core.Model;

namespace NoDrive.Core.Index;

/// <summary>
/// The disk-backed item index (systemPatterns 2.6, ADR-0005): the tree never lives in RAM. One writer connection guarded by a lock,
/// short-lived reader connections (WAL allows concurrent readers), a small page cache, and one transaction per delta page with the
/// resume token inside it so a crash resumes exactly.
/// The host must initialise the SQLitePCL provider before the first use.
/// </summary>
public sealed record RemoteOp(long Seq, string Kind, string ItemId, string? Target, int Attempts);

public sealed class ItemIndex : IDisposable
{
    private const int SchemaVersion = 4;
    private static readonly string[] UpsertParameters = ["$id", "$p", "$n", "$s", "$m", "$f", "$e", "$c", "$q", "$g"];
    private readonly string _path;
    private readonly SqliteConnection _writer;
    private readonly object _writeLock = new();

    public ItemIndex(string path)
    {
        _path = path;
        _writer = Open(path);
        Migrate();
    }

    public string Path => _path;

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        // 4 MiB cache per connection keeps memory independent of the item count (measured: 500k items, 10 MB settled).
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-4096; PRAGMA mmap_size=0; PRAGMA temp_store=FILE; PRAGMA wal_autocheckpoint=2000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        lock (_writeLock)
        {
            int version = (int)Scalar<long>(_writer, "PRAGMA user_version");
            if (version >= SchemaVersion) { return; }
            using var tx = _writer.BeginTransaction();
            Exec(_writer, """
                CREATE TABLE IF NOT EXISTS items(
                  id TEXT PRIMARY KEY NOT NULL,
                  parent_id TEXT,
                  name TEXT NOT NULL,
                  size INTEGER NOT NULL,
                  mtime INTEGER NOT NULL,
                  is_folder INTEGER NOT NULL,
                  etag TEXT, ctag TEXT, qxor TEXT,
                  populated INTEGER NOT NULL DEFAULT 0,
                  gen INTEGER NOT NULL DEFAULT 0
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_items_parent_name ON items(parent_id, name);
                CREATE TABLE IF NOT EXISTS uploads(path TEXT PRIMARY KEY NOT NULL, depth INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, due INTEGER NOT NULL) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS remote_ops(seq INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, item_id TEXT NOT NULL, target TEXT, attempts INTEGER NOT NULL DEFAULT 0, due INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY NOT NULL, v TEXT) WITHOUT ROWID;
                """);
            // v4: `local` = a placeholder or file for this item exists on disk (set when we create or link it). Only such items can be judged "deleted locally".
            bool hasLocal = false;
            using (var info = _writer.CreateCommand())
            {
                info.CommandText = "SELECT 1 FROM pragma_table_info('items') WHERE name='local'";
                hasLocal = info.ExecuteScalar() is not null;
            }
            if (!hasLocal) { Exec(_writer, "ALTER TABLE items ADD COLUMN local INTEGER NOT NULL DEFAULT 0"); }
            Exec(_writer, $"PRAGMA user_version={SchemaVersion}");
            tx.Commit();
        }
    }

    // ---------------- metadata (resume tokens, generation, account) ----------------

    public string? GetMeta(string key)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string? value)
    {
        lock (_writeLock)
        {
            using var cmd = _writer.CreateCommand();
            if (value is null)
            {
                cmd.CommandText = "DELETE FROM meta WHERE k=$k";
                cmd.Parameters.AddWithValue("$k", key);
            }
            else
            {
                cmd.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
            }
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The current enumeration generation; a full resync increments it and prunes rows that were not seen again.</summary>
    public int Generation => int.TryParse(GetMeta("generation"), out var g) ? g : 0;

    // ---------------- writes ----------------

    /// <summary>
    /// Applies one delta page and a metadata entry (the resume token) in a single transaction. Deletions first, then upserts;
    /// the last occurrence of an id wins. Rows touched are stamped with <paramref name="generation"/>. Returns (upserts, deletions that hit a row).
    /// </summary>
    public (int Upserts, int Deletions) ApplyPage(IReadOnlyList<DeltaChange> changes, string metaKey, string metaValue, int generation)
    {
        lock (_writeLock)
        {
            using var tx = _writer.BeginTransaction();
            int ups = 0, dels = 0;

            using (var del = _writer.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM items WHERE id=$id";
                var p = del.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
                foreach (var ch in changes)
                {
                    if (!ch.IsDeletion) { continue; }
                    p.Value = ch.Id;
                    dels += del.ExecuteNonQuery();
                }
            }

            using (var up = _writer.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO items(id,parent_id,name,size,mtime,is_folder,etag,ctag,qxor,gen) VALUES($id,$p,$n,$s,$m,$f,$e,$c,$q,$g)
                    ON CONFLICT(id) DO UPDATE SET parent_id=$p,name=$n,size=$s,mtime=$m,is_folder=$f,etag=$e,ctag=$c,qxor=$q,gen=$g
                    """;
                var ps = UpsertParameters.Select(n => up.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
                foreach (var ch in changes)
                {
                    if (ch.Item is not { } it) { continue; }
                    ps[0].Value = it.Id;
                    ps[1].Value = (object?)it.ParentId ?? DBNull.Value;
                    ps[2].Value = it.Name;
                    ps[3].Value = it.Size;
                    ps[4].Value = it.ModifiedUtc.UtcTicks;
                    ps[5].Value = it.IsFolder ? 1 : 0;
                    ps[6].Value = (object?)it.ETag ?? DBNull.Value;
                    ps[7].Value = (object?)it.CTag ?? DBNull.Value;
                    ps[8].Value = (object?)it.QuickXorHash ?? DBNull.Value;
                    ps[9].Value = generation;
                    up.ExecuteNonQuery();
                    ups++;
                }
            }

            using (var meta = _writer.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v";
                meta.Parameters.AddWithValue("$k", metaKey);
                meta.Parameters.AddWithValue("$v", metaValue);
                meta.ExecuteNonQuery();
            }
            tx.Commit();
            return (ups, dels);
        }
    }

    /// <summary>After a full re-enumeration: remove every row not touched in <paramref name="generation"/> (it vanished remotely while we had no valid token).</summary>
    public int PruneOlderThan(int generation)
    {
        lock (_writeLock)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE gen < $g";
            cmd.Parameters.AddWithValue("$g", generation);
            return cmd.ExecuteNonQuery();
        }
    }

    public void SetPopulated(string folderId, bool populated)
    {
        lock (_writeLock)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = "UPDATE items SET populated=$p WHERE id=$id";
            cmd.Parameters.AddWithValue("$p", populated ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", folderId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Forget all population state (after the sync root was re-registered or its placeholders were removed).</summary>
    public void ResetPopulation()
    {
        lock (_writeLock) { Exec(_writer, "UPDATE items SET populated=0"); }
    }

    public void Checkpoint()
    {
        lock (_writeLock) { Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);"); }
    }

    // ---------------- reads (own short-lived connection each: never blocks the writer, bounded memory) ----------------

    public long Count()
    {
        using var c = Open(_path);
        return Scalar<long>(c, "SELECT COUNT(*) FROM items");
    }

    public IndexedItem? TryGet(string id)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public long CountChildren(string parentId)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE parent_id=$p";
        cmd.Parameters.AddWithValue("$p", parentId);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Case-insensitive child lookup (OneDrive names are case-insensitive).</summary>
    public IndexedItem? FindChild(string parentId, string name)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE parent_id=$p AND name=$n COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$p", parentId);
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    // ---------------- upload journal (journal before mutate; survives crashes) ----------------

    /// <summary>Queues local paths for upload (idempotent: an already queued path keeps its schedule). One transaction for the whole batch.</summary>
    public void EnqueueUploads(IEnumerable<string> paths, long dueTicks)
    {
        lock (_writeLock)
        {
            using var tx = _writer.BeginTransaction();
            using var cmd = _writer.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO uploads(path,depth,attempts,due) VALUES($p,$d,0,$t)";
            var p = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$p" });
            var d = cmd.Parameters.Add(new SqliteParameter { ParameterName = "$d" });
            cmd.Parameters.AddWithValue("$t", dueTicks);
            foreach (var path in paths)
            {
                p.Value = path;
                d.Value = path.Count(ch => ch == '\\');
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Queued uploads that are due, shallowest first (parents before children).</summary>
    public IReadOnlyList<(string Path, int Attempts)> DueUploads(long nowTicks, int max)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path,attempts FROM uploads WHERE due<=$n ORDER BY depth,due LIMIT $m";
        cmd.Parameters.AddWithValue("$n", nowTicks);
        cmd.Parameters.AddWithValue("$m", max);
        var list = new List<(string, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) { list.Add((r.GetString(0), r.GetInt32(1))); }
        return list;
    }

    public void RescheduleUpload(string path, int attempts, long dueTicks) => ExecWrite("UPDATE uploads SET attempts=$a,due=$t WHERE path=$p", ("$a", attempts), ("$t", dueTicks), ("$p", path));

    public void RemoveUpload(string path) => ExecWrite("DELETE FROM uploads WHERE path=$p", ("$p", path));

    public long CountUploads()
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM uploads";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---------------- local-change journal (delete/rename made in Explorer, replayed to the cloud) ----------------

    public void EnqueueOp(string kind, string itemId, string? target, long dueTicks) =>
        ExecWrite("INSERT INTO remote_ops(kind,item_id,target,attempts,due) VALUES($k,$i,$t,0,$d)", ("$k", kind), ("$i", itemId), ("$t", (object?)target ?? DBNull.Value), ("$d", dueTicks));

    /// <summary>Due operations in arrival order; an operation never overtakes an earlier one on the same item.</summary>
    public IReadOnlyList<RemoteOp> DueOps(long nowTicks, int max)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq,kind,item_id,target,attempts FROM remote_ops o WHERE due<=$n AND NOT EXISTS (SELECT 1 FROM remote_ops e WHERE e.item_id=o.item_id AND e.seq<o.seq) ORDER BY seq LIMIT $m";
        cmd.Parameters.AddWithValue("$n", nowTicks);
        cmd.Parameters.AddWithValue("$m", max);
        var list = new List<RemoteOp>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) { list.Add(new RemoteOp(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4))); }
        return list;
    }

    public void RescheduleOp(long seq, int attempts, long dueTicks) => ExecWrite("UPDATE remote_ops SET attempts=$a,due=$d WHERE seq=$s", ("$a", attempts), ("$d", dueTicks), ("$s", seq));

    public void RemoveOp(long seq) => ExecWrite("DELETE FROM remote_ops WHERE seq=$s", ("$s", seq));

    public long CountOps()
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM remote_ops";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>True when an operation of the given kind (delete or rename) is queued for the item.</summary>
    public bool HasPendingOp(string itemId, string kind)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM remote_ops WHERE item_id=$i AND kind=$k LIMIT 1";
        cmd.Parameters.AddWithValue("$i", itemId);
        cmd.Parameters.AddWithValue("$k", kind);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Cancels queued operations of a kind for an item (for example a delete that turned out to be a safe-save).</summary>
    public void RemoveOps(string itemId, string kind) => ExecWrite("DELETE FROM remote_ops WHERE item_id=$i AND kind=$k", ("$i", itemId), ("$k", kind));

    /// <summary>After the network or the PC comes back: make every deferred upload and operation due now so it does not wait out its backoff.</summary>
    public void ReleaseBackoff(long nowTicks)
    {
        ExecWrite("UPDATE uploads SET due=$n WHERE due>$n", ("$n", nowTicks));
        ExecWrite("UPDATE remote_ops SET due=$n WHERE due>$n", ("$n", nowTicks));
    }

    // ---------------- "exists on disk" tracking (offline reconcile) ----------------

    public void MarkLocal(string itemId) => ExecWrite("UPDATE items SET local=1 WHERE id=$i AND local=0", ("$i", itemId));

    /// <summary>All children of a folder now have a placeholder on disk (the folder was populated).</summary>
    public void MarkChildrenLocal(string parentId) => ExecWrite("UPDATE items SET local=1 WHERE parent_id=$p AND local=0", ("$p", parentId));

    public void MarkNotLocal(string itemId) => ExecWrite("UPDATE items SET local=0 WHERE id=$i", ("$i", itemId));

    /// <summary>Children known to exist on disk (the reconcile compares these with the real folder).</summary>
    public IEnumerable<IndexedItem> EnumerateLocalChildren(string parentId)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE parent_id=$p AND local=1";
        cmd.Parameters.AddWithValue("$p", parentId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) { yield return Read(r); }
    }

    public long CountLocal()
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items WHERE local=1";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs SQLite's quick integrity check; returns null when healthy, otherwise the first problem text.</summary>
    public string? QuickCheck()
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check(1)";
        string? first = cmd.ExecuteScalar() as string;
        return string.Equals(first, "ok", StringComparison.OrdinalIgnoreCase) ? null : first ?? "unknown";
    }

    public bool HasPendingOp(string itemId)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM remote_ops WHERE item_id=$i LIMIT 1";
        cmd.Parameters.AddWithValue("$i", itemId);
        return cmd.ExecuteScalar() is not null;
    }

    private void ExecWrite(string sql, params (string Name, object Value)[] args)
    {
        lock (_writeLock)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) { cmd.Parameters.AddWithValue(n, v); }
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Streams a folder's children ordered by name; nothing is materialised (this feeds FETCH_PLACEHOLDERS).</summary>
    public IEnumerable<IndexedItem> EnumerateChildren(string parentId)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE parent_id=$p ORDER BY name";
        cmd.Parameters.AddWithValue("$p", parentId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) { yield return Read(r); }
    }

    /// <summary>Folders whose child count is at least <paramref name="minChildren"/>, largest first, with their counts.</summary>
    public IReadOnlyList<(string FolderId, long Children)> HeavyFolders(long minChildren)
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT parent_id, COUNT(*) n FROM items WHERE parent_id IS NOT NULL GROUP BY parent_id HAVING n >= $t ORDER BY n DESC";
        cmd.Parameters.AddWithValue("$t", minChildren);
        var list = new List<(string, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) { list.Add((r.GetString(0), r.GetInt64(1))); }
        return list;
    }

    /// <summary>The chain of folder ids from the first folder below the root down to <paramref name="id"/> (inclusive), root excluded.</summary>
    public IReadOnlyList<string> PathTo(string id, string rootId)
    {
        var chain = new List<string>();
        string? current = id;
        int guard = 0;
        while (current is not null && current != rootId && guard++ < 512)
        {
            chain.Add(current);
            current = TryGet(current)?.ParentId;
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>Ids of folders whose placeholders have already been created on disk.</summary>
    public HashSet<string> PopulatedFolderIds()
    {
        using var c = Open(_path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM items WHERE populated=1";
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read()) { set.Add(r.GetString(0)); }
        return set;
    }

    public string? GetName(string id) => TryGet(id)?.Name;

    private const string SelectColumns = "SELECT id,parent_id,name,size,mtime,is_folder,etag,ctag,qxor,populated,local FROM items";

    private static IndexedItem Read(SqliteDataReader r) => new(
        r.GetString(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.GetString(2),
        r.GetInt64(3),
        r.GetInt64(4),
        r.GetInt64(5) != 0,
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetInt64(9) != 0,
        r.GetInt64(10) != 0);

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose() => _writer.Dispose();
}
