using Microsoft.Data.Sqlite;

namespace NoDrive.Spikes.Scale;

/// <summary>
/// The disk-backed item index the engine will use (systemPatterns 2.6): the tree never lives in RAM. Bounded page cache,
/// WAL, one transaction per delta page, the nextLink persisted in the SAME transaction so a crash resumes exactly.
/// </summary>
internal sealed class ItemStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly SqliteCommand _upsert;
    private readonly SqliteCommand _delete;
    private readonly SqliteCommand _setMeta;

    public static void InitProvider()
    {
#if WINSQLITE
        // Windows: the system SQLite (System32\winsqlite3.dll), zero native payload shipped with the app.
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
    }

    public ItemStore(string path)
    {
        _db = new SqliteConnection($"Data Source={path};Pooling=False");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-4096; PRAGMA mmap_size=0; PRAGMA temp_store=FILE; PRAGMA wal_autocheckpoint=2000;");
        Exec("""
            CREATE TABLE IF NOT EXISTS items(
              id TEXT PRIMARY KEY NOT NULL,
              parent_id TEXT,
              name TEXT NOT NULL,
              size INTEGER NOT NULL,
              mtime TEXT,
              is_folder INTEGER NOT NULL,
              etag TEXT, ctag TEXT, qxor TEXT,
              state INTEGER NOT NULL DEFAULT 0
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_items_parent_name ON items(parent_id, name);
            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY NOT NULL, v TEXT) WITHOUT ROWID;
            """);

        _upsert = _db.CreateCommand();
        _upsert.CommandText = """
            INSERT INTO items(id,parent_id,name,size,mtime,is_folder,etag,ctag,qxor) VALUES($id,$p,$n,$s,$m,$f,$e,$c,$q)
            ON CONFLICT(id) DO UPDATE SET parent_id=$p,name=$n,size=$s,mtime=$m,is_folder=$f,etag=$e,ctag=$c,qxor=$q
            """;
        foreach (var n in new[] { "$id", "$p", "$n", "$s", "$m", "$f", "$e", "$c", "$q" }) { _upsert.Parameters.Add(new SqliteParameter { ParameterName = n }); }
        _upsert.Prepare();

        _delete = _db.CreateCommand();
        _delete.CommandText = "DELETE FROM items WHERE id=$id";
        _delete.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
        _delete.Prepare();

        _setMeta = _db.CreateCommand();
        _setMeta.CommandText = "INSERT INTO meta(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v";
        _setMeta.Parameters.Add(new SqliteParameter { ParameterName = "$k" });
        _setMeta.Parameters.Add(new SqliteParameter { ParameterName = "$v" });
        _setMeta.Prepare();
    }

    public void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT v FROM meta WHERE k=$k";
        c.Parameters.AddWithValue("$k", key);
        return c.ExecuteScalar() as string;
    }

    public long Count()
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM items";
        return (long)c.ExecuteScalar()!;
    }

    /// <summary>Applies one delta page and the resume token atomically. Deleted items first, then upserts, last occurrence wins.</summary>
    public (int upserts, int deletes) ApplyPage(DeltaPage page, string metaKey, string metaValue)
    {
        using var tx = _db.BeginTransaction();
        _delete.Transaction = tx; _upsert.Transaction = tx; _setMeta.Transaction = tx;
        int ups = 0, dels = 0;
        foreach (var it in page.Value)
        {
            if (it.Deleted is null) { continue; }
            _delete.Parameters["$id"].Value = it.Id;
            dels += _delete.ExecuteNonQuery();
        }
        foreach (var it in page.Value)
        {
            if (it.Deleted is not null) { continue; }
            var p = _upsert.Parameters;
            p["$id"].Value = it.Id;
            p["$p"].Value = (object?)it.Parent?.Id ?? DBNull.Value;
            p["$n"].Value = it.Name ?? "";
            p["$s"].Value = it.Size;
            p["$m"].Value = (object?)it.LastModified ?? DBNull.Value;
            p["$f"].Value = it.Folder is null ? 0 : 1;
            p["$e"].Value = (object?)it.ETag ?? DBNull.Value;
            p["$c"].Value = (object?)it.CTag ?? DBNull.Value;
            p["$q"].Value = (object?)it.File?.Hashes?.QuickXorHash ?? DBNull.Value;
            _upsert.ExecuteNonQuery();
            ups++;
        }
        _setMeta.Parameters["$k"].Value = metaKey;
        _setMeta.Parameters["$v"].Value = metaValue;
        _setMeta.ExecuteNonQuery();
        tx.Commit();
        return (ups, dels);
    }

    /// <summary>What FETCH_PLACEHOLDERS would do: stream a folder's children in batches without loading them all.</summary>
    public (long rows, double firstBatchMs, double totalMs) ListChildren(string parentId, int batch = 512)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,name,size,mtime,is_folder FROM items WHERE parent_id=$p ORDER BY name";
        c.Parameters.AddWithValue("$p", parentId);
        using var r = c.ExecuteReader();
        long rows = 0; double first = -1; long checksum = 0;
        while (r.Read())
        {
            rows++;
            checksum += r.GetString(1).Length + r.GetInt64(2);   // touch the data like CfCreatePlaceholders would
            if (rows == batch && first < 0) { first = sw.Elapsed.TotalMilliseconds; }
        }
        if (first < 0) { first = sw.Elapsed.TotalMilliseconds; }
        GC.KeepAlive(checksum);
        return (rows, first, sw.Elapsed.TotalMilliseconds);
    }

    public string? BiggestFolder(out long children)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT parent_id, COUNT(*) n FROM items GROUP BY parent_id ORDER BY n DESC LIMIT 1";
        using var r = c.ExecuteReader();
        children = 0;
        if (!r.Read()) { return null; }
        children = r.GetInt64(1);
        return r.GetString(0);
    }

    public double RandomLookups(IReadOnlyList<string> ids, int count)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT name,size,etag FROM items WHERE id=$id";
        var p = c.Parameters.Add(new SqliteParameter { ParameterName = "$id" });
        c.Prepare();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long hit = 0;
        for (int i = 0; i < count; i++)
        {
            p.Value = ids[(i * 7919) % ids.Count];
            using var r = c.ExecuteReader();
            if (r.Read()) { hit++; }
        }
        if (hit != count) { throw new InvalidOperationException($"lookups: {hit}/{count} hit"); }
        return sw.Elapsed.TotalMilliseconds;
    }

    public List<string> SampleIds(int n)
    {
        var list = new List<string>(n);
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id FROM items LIMIT $n";
        c.Parameters.AddWithValue("$n", n);
        using var r = c.ExecuteReader();
        while (r.Read()) { list.Add(r.GetString(0)); }
        return list;
    }

    public void Checkpoint() => Exec("PRAGMA wal_checkpoint(TRUNCATE);");

    public void Dispose()
    {
        _upsert.Dispose(); _delete.Dispose(); _setMeta.Dispose(); _db.Dispose();
    }
}
