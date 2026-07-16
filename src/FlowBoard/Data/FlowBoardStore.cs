using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using FlowBoard.Domain;
using Microsoft.Data.Sqlite;

namespace FlowBoard.Data;

/// <summary>
/// The only thing that talks to SQLite. One long-lived connection (single-process app),
/// WAL journalling, and one explicit transaction per unit of work so a crash mid-write
/// can never leave a half-applied change on disk.
/// </summary>
public sealed class FlowBoardStore : IDisposable
{
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;

    public string DatabasePath { get; }

    public FlowBoardStore(string? path = null)
    {
        DatabasePath = path ?? DefaultDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
        }.ToString());

        _conn.Open();

        // WAL survives the process; NORMAL is safe under WAL (only loses the last
        // transaction on OS crash, never corrupts). busy_timeout guards the brief
        // window where a checkpoint holds the write lock.
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA foreign_keys=ON;");
        Exec("PRAGMA busy_timeout=3000;");

        Migrate();
    }

    public static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlowBoard", "flowboard.db");

    // ---------------------------------------------------------------- migrations

    /// <summary>
    /// v1 -> v2: cards gain a start date.
    ///
    /// CREATE TABLE IF NOT EXISTS in Schema.sql only creates tables that don't exist — it
    /// will not add a column to a table that already does. So an existing database needs
    /// this ALTER explicitly, and the check has to be the column list rather than the
    /// version, because a v1 file that Schema.sql just ran against still reports v1.
    /// </summary>
    private void MigrateV1ToV2(SqliteTransaction tx)
    {
        if (HasColumn(tx, "cards", "start_utc")) return;

        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "ALTER TABLE cards ADD COLUMN start_utc TEXT;";
        cmd.ExecuteNonQuery();
    }

    private bool HasColumn(SqliteTransaction tx, string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info({table});";

        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void Migrate()
    {
        using var tx = _conn.BeginTransaction();

        var sql = ReadEmbedded("FlowBoard.Data.Schema.sql");
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        var current = ReadMeta(tx, "schema_version");
        if (current is null)
        {
            WriteMeta(tx, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            var v = int.Parse(current, CultureInfo.InvariantCulture);
            if (v > SchemaVersion)
                throw new InvalidOperationException(
                    $"Database schema v{v} is newer than this build (v{SchemaVersion}). Upgrade FlowBoard.");

            // Forward-only migration ladder. Each step takes v -> v+1, and the whole ladder
            // runs inside the caller's transaction, so a failure halfway up leaves the
            // database exactly where it started rather than stranded between versions.
            while (v < SchemaVersion)
            {
                switch (v)
                {
                    case 1: MigrateV1ToV2(tx); break;
                    default:
                        throw new InvalidOperationException($"No migration from schema v{v}.");
                }
                v++;
            }

            WriteMeta(tx, "schema_version", v.ToString(CultureInfo.InvariantCulture));
        }

        tx.Commit();
    }

    // ---------------------------------------------------------------- unit of work

    /// <summary>Runs <paramref name="work"/> inside a single transaction. Any exception
    /// rolls the whole thing back — callers get all-or-nothing semantics per op.</summary>
    public void InTransaction(Action<IDbTransaction> work)
    {
        using var tx = _conn.BeginTransaction();
        work(tx);
        tx.Commit();
    }

    // ---------------------------------------------------------------- load

    public BoardModel Load()
    {
        var model = new BoardModel();

        foreach (var l in Query("SELECT * FROM labels ORDER BY position", r => new Label
        {
            Id = r.Str("id"),
            Name = r.Str("name"),
            Color = r.Str("color"),
            Position = r.Int("position"),
            CreatedUtc = Time(r, "created_utc"),
            ModifiedUtc = Time(r, "modified_utc"),
        }))
            model.Labels.Add(l);

        var boards = Query("SELECT * FROM boards ORDER BY position", r => new Board
        {
            Id = r.Str("id"),
            WorkspaceId = r.Str("workspace_id"),
            Name = r.Str("name"),
            Accent = r.Str("accent"),
            Position = r.Int("position"),
            WipLimit = r.Int("wip_limit"),
            Archived = r.Bool("archived"),
            CreatedUtc = Time(r, "created_utc"),
            ModifiedUtc = Time(r, "modified_utc"),
        }).ToList();

        var cards = Query("SELECT * FROM cards ORDER BY position", r => new Card
        {
            Id = r.Str("id"),
            BoardId = r.Str("board_id"),
            Title = r.Str("title"),
            Description = r.Str("description"),
            Priority = (Priority)r.Int("priority"),
            StartUtc = NullableTime(r, "start_utc"),
            DueUtc = NullableTime(r, "due_utc"),
            Position = r.Int("position"),
            Archived = r.Bool("archived"),
            CreatedUtc = Time(r, "created_utc"),
            ModifiedUtc = Time(r, "modified_utc"),
            TouchedUtc = Time(r, "touched_utc"),
        }).ToList();

        var cardsById = cards.ToDictionary(c => c.Id);

        foreach (var (cardId, labelId) in Query(
                     "SELECT card_id, label_id FROM card_labels",
                     r => (r.Str("card_id"), r.Str("label_id"))))
            if (cardsById.TryGetValue(cardId, out var c)) c.LabelIds.Add(labelId);

        foreach (var i in Query("SELECT * FROM checklist_items ORDER BY position", r => new ChecklistItem
        {
            Id = r.Str("id"),
            CardId = r.Str("card_id"),
            Text = r.Str("text"),
            Done = r.Bool("done"),
            Position = r.Int("position"),
            CreatedUtc = Time(r, "created_utc"),
            ModifiedUtc = Time(r, "modified_utc"),
        }))
            if (cardsById.TryGetValue(i.CardId, out var c)) c.Checklist.Add(i);

        foreach (var l in Query("SELECT * FROM card_links ORDER BY position", r => new CardLink
        {
            Id = r.Str("id"),
            CardId = r.Str("card_id"),
            Kind = (LinkKind)r.Int("kind"),
            Target = r.Str("target"),
            Display = r.Str("display"),
            Position = r.Int("position"),
            CreatedUtc = Time(r, "created_utc"),
            ModifiedUtc = Time(r, "modified_utc"),
        }))
            if (cardsById.TryGetValue(l.CardId, out var c)) c.Links.Add(l);

        foreach (var a in Query("SELECT * FROM activities ORDER BY created_utc", r => new Activity
        {
            Id = r.Str("id"),
            CardId = r.Str("card_id"),
            Kind = (ActivityKind)r.Int("kind"),
            Detail = r.Str("detail"),
            CreatedUtc = Time(r, "created_utc"),
            ModifiedUtc = Time(r, "modified_utc"),
        }))
            if (cardsById.TryGetValue(a.CardId, out var c)) c.Activities.Add(a);

        // Archived cards stay out of the lane; the Archive view queries them separately.
        foreach (var b in boards)
            foreach (var card in cards.Where(c => c.BoardId == b.Id && !c.Archived).OrderBy(c => c.Position))
                b.Cards.Add(card);

        foreach (var ws in Query("SELECT * FROM workspaces ORDER BY position", r => new Workspace
        {
            Id = r.Str("id"),
            Name = r.Str("name"),
            Position = r.Int("position"),
            Archived = r.Bool("archived"),
            CreatedUtc = Time(r, "created_utc"),
            ModifiedUtc = Time(r, "modified_utc"),
        }))
        {
            foreach (var b in boards.Where(b => b.WorkspaceId == ws.Id).OrderBy(b => b.Position))
                ws.Boards.Add(b);

            model.Workspaces.Add(ws);
            model.Index(ws);
        }

        // Archived cards are still reachable by id (Archive view, undo of an archive).
        foreach (var c in cards) model.CardsById[c.Id] = c;

        return model;
    }

    /// <summary>Archived cards, newest first — the Archive view's data source.</summary>
    public IEnumerable<Card> LoadArchivedCards(BoardModel model) =>
        model.CardsById.Values.Where(c => c.Archived).OrderByDescending(c => c.ModifiedUtc);

    // ---------------------------------------------------------------- upserts

    public void Upsert(IDbTransaction tx, Workspace w) => Write(tx,
        """
        INSERT INTO workspaces (id,name,position,archived,created_utc,modified_utc)
        VALUES ($id,$name,$pos,$arc,$cre,$mod)
        ON CONFLICT(id) DO UPDATE SET name=$name, position=$pos, archived=$arc, modified_utc=$mod;
        """,
        ("$id", w.Id), ("$name", w.Name), ("$pos", w.Position), ("$arc", w.Archived),
        ("$cre", Iso(w.CreatedUtc)), ("$mod", Iso(w.ModifiedUtc)));

    public void Upsert(IDbTransaction tx, Board b) => Write(tx,
        """
        INSERT INTO boards (id,workspace_id,name,accent,position,wip_limit,archived,created_utc,modified_utc)
        VALUES ($id,$ws,$name,$acc,$pos,$wip,$arc,$cre,$mod)
        ON CONFLICT(id) DO UPDATE SET workspace_id=$ws, name=$name, accent=$acc, position=$pos,
                                      wip_limit=$wip, archived=$arc, modified_utc=$mod;
        """,
        ("$id", b.Id), ("$ws", b.WorkspaceId), ("$name", b.Name), ("$acc", b.Accent),
        ("$pos", b.Position), ("$wip", b.WipLimit), ("$arc", b.Archived),
        ("$cre", Iso(b.CreatedUtc)), ("$mod", Iso(b.ModifiedUtc)));

    public void Upsert(IDbTransaction tx, Card c) => Write(tx,
        """
        INSERT INTO cards (id,board_id,title,description,priority,start_utc,due_utc,position,archived,
                           created_utc,modified_utc,touched_utc)
        VALUES ($id,$b,$t,$d,$p,$start,$due,$pos,$arc,$cre,$mod,$tch)
        ON CONFLICT(id) DO UPDATE SET board_id=$b, title=$t, description=$d, priority=$p,
                                      start_utc=$start, due_utc=$due, position=$pos, archived=$arc,
                                      modified_utc=$mod, touched_utc=$tch;
        """,
        ("$id", c.Id), ("$b", c.BoardId), ("$t", c.Title), ("$d", c.Description),
        ("$p", (int)c.Priority),
        ("$start", c.StartUtc is null ? DBNull.Value : Iso(c.StartUtc.Value)),
        ("$due", c.DueUtc is null ? DBNull.Value : Iso(c.DueUtc.Value)),
        ("$pos", c.Position), ("$arc", c.Archived), ("$cre", Iso(c.CreatedUtc)),
        ("$mod", Iso(c.ModifiedUtc)), ("$tch", Iso(c.TouchedUtc)));

    public void Upsert(IDbTransaction tx, Label l) => Write(tx,
        """
        INSERT INTO labels (id,name,color,position,created_utc,modified_utc)
        VALUES ($id,$n,$c,$pos,$cre,$mod)
        ON CONFLICT(id) DO UPDATE SET name=$n, color=$c, position=$pos, modified_utc=$mod;
        """,
        ("$id", l.Id), ("$n", l.Name), ("$c", l.Color), ("$pos", l.Position),
        ("$cre", Iso(l.CreatedUtc)), ("$mod", Iso(l.ModifiedUtc)));

    public void Upsert(IDbTransaction tx, ChecklistItem i) => Write(tx,
        """
        INSERT INTO checklist_items (id,card_id,text,done,position,created_utc,modified_utc)
        VALUES ($id,$c,$t,$d,$pos,$cre,$mod)
        ON CONFLICT(id) DO UPDATE SET card_id=$c, text=$t, done=$d, position=$pos, modified_utc=$mod;
        """,
        ("$id", i.Id), ("$c", i.CardId), ("$t", i.Text), ("$d", i.Done), ("$pos", i.Position),
        ("$cre", Iso(i.CreatedUtc)), ("$mod", Iso(i.ModifiedUtc)));

    public void Upsert(IDbTransaction tx, CardLink l) => Write(tx,
        """
        INSERT INTO card_links (id,card_id,kind,target,display,position,created_utc,modified_utc)
        VALUES ($id,$c,$k,$t,$disp,$pos,$cre,$mod)
        ON CONFLICT(id) DO UPDATE SET card_id=$c, kind=$k, target=$t, display=$disp,
                                      position=$pos, modified_utc=$mod;
        """,
        ("$id", l.Id), ("$c", l.CardId), ("$k", (int)l.Kind), ("$t", l.Target),
        ("$disp", l.Display), ("$pos", l.Position), ("$cre", Iso(l.CreatedUtc)), ("$mod", Iso(l.ModifiedUtc)));

    public void Upsert(IDbTransaction tx, Activity a) => Write(tx,
        """
        INSERT INTO activities (id,card_id,kind,detail,created_utc,modified_utc)
        VALUES ($id,$c,$k,$d,$cre,$mod)
        ON CONFLICT(id) DO UPDATE SET detail=$d, modified_utc=$mod;
        """,
        ("$id", a.Id), ("$c", a.CardId), ("$k", (int)a.Kind), ("$d", a.Detail),
        ("$cre", Iso(a.CreatedUtc)), ("$mod", Iso(a.ModifiedUtc)));

    public void SetCardLabels(IDbTransaction tx, Card c)
    {
        Write(tx, "DELETE FROM card_labels WHERE card_id=$c;", ("$c", c.Id));
        foreach (var id in c.LabelIds)
            Write(tx, "INSERT OR IGNORE INTO card_labels (card_id,label_id) VALUES ($c,$l);",
                ("$c", c.Id), ("$l", id));
    }

    public void Delete(IDbTransaction tx, string table, string id) =>
        Write(tx, $"DELETE FROM {table} WHERE id=$id;", ("$id", id));

    /// <summary>Used by import: wipes user data but keeps the schema and version row.</summary>
    public void Clear(IDbTransaction tx)
    {
        foreach (var t in new[] { "activities", "card_links", "checklist_items", "card_labels",
                                  "cards", "boards", "workspaces", "labels" })
            Write(tx, $"DELETE FROM {t};");
    }

    // ---------------------------------------------------------------- plumbing

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Write(IDbTransaction tx, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private IEnumerable<T> Query<T>(string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var result = new List<T>();
        while (r.Read()) result.Add(map(r));
        return result;
    }

    private string? ReadMeta(IDbTransaction tx, string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void WriteMeta(IDbTransaction tx, string key, string value) => Write(tx,
        "INSERT INTO meta (key,value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v;",
        ("$k", key), ("$v", value));

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Read a UTC timestamp written by <see cref="Iso"/>.
    ///
    /// RoundtripKind and AdjustToUniversal are mutually exclusive, and DateTime.Parse
    /// validates the flag combination *before* it looks at the string — so passing both
    /// throws unconditionally, on every call, whatever the input. The bug is invisible
    /// against an empty database (no rows, no parses) and fires on the first read of the
    /// first saved row, i.e. on the second launch. Nothing in a compile or a static check
    /// can see it; only running the app twice can.
    ///
    /// RoundtripKind alone is correct here: the "o" format carries the Z, so the parse
    /// already yields Kind=Utc. The switch is belt and braces for rows written by an older
    /// build, or hand-edited, where the offset may be missing.
    /// </summary>
    private static DateTime Time(SqliteDataReader r, string col)
    {
        var parsed = DateTime.Parse(r.Str(col), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        return parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)   // no offset: it was written as UTC
        };
    }

    private static DateTime? NullableTime(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : Time(r, col);
    }

    private static string ReadEmbedded(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource '{name}'.");
        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }

    public void Dispose()
    {
        // Fold the WAL back into the main db so a copied/backed-up .db file is complete.
        try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { /* closing anyway */ }
        _conn.Dispose();
    }
}

internal static class ReaderExtensions
{
    public static string Str(this SqliteDataReader r, string col) => r.GetString(r.GetOrdinal(col));
    public static int Int(this SqliteDataReader r, string col) => r.GetInt32(r.GetOrdinal(col));
    public static bool Bool(this SqliteDataReader r, string col) => r.GetInt32(r.GetOrdinal(col)) != 0;
}
