using Microsoft.Data.Sqlite;
using Shiny;
using Shiny.DocumentDb;

namespace GitHubShine.Settings;

public interface IDatabaseBackup
{
    /// <summary>Writes a snapshot of every document in the store — accounts, tokens, people, sync mappings, preferences and seen-state.</summary>
    Task BackupToAsync(string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Merges a backup file into the live store (replace-on-conflict per document), then reloads
    /// the stores that cache their rows in memory. Returns the number of documents written.
    /// </summary>
    Task<int> RestoreFromAsync(string sourcePath, CancellationToken ct = default);
}

/// <summary>
/// Backup and restore through DocumentDb's own <see cref="IDocumentBackup"/> export format: a
/// streamed JSON document of <c>{ id, docType, data }</c> records covering every document table.
/// </summary>
/// <remarks>
/// This replaced a hand-rolled SQLite path (VACUUM INTO out, ATTACH + INSERT OR REPLACE back in).
/// The store's own format is preferred because it knows what a document is: it enumerates the
/// registered types rather than a hard-coded table list that had already gone stale — followed
/// people and sync mappings were being written into backups and then silently skipped on the way
/// back in — and it rebuilds the sidecar tables (history, full-text, spatial, vector) through the
/// write path instead of copying rows behind the store's back.
/// <para>
/// Note the trade the import lane makes for its speed: it binds document bodies verbatim and does
/// not run versioning/CAS, temporal history, interceptors or global query filters. That suits a
/// restore, which is meant to reinstate exactly what was saved, but it is not a general write path.
/// </para>
/// </remarks>
[Singleton]
public sealed class DocumentDatabaseBackup(
    IDocumentStore store,
    IConfigStore config,
    IGitProviderFactory factory,
    IPersonStore persons,
    ISyncStore sync,
    INotificationPrefsStore notificationPrefs) : IDatabaseBackup
{
    // Bulk export/import is a capability a provider opts into rather than part of IDocumentStore,
    // so it is probed for rather than injected. The SQLite provider implements it; this only
    // trips if the store is ever swapped for one that doesn't.
    IDocumentBackup Backup => store as IDocumentBackup
        ?? throw new NotSupportedException("The configured document store does not support bulk export/import.");

    public async Task BackupToAsync(string destinationPath, CancellationToken ct = default)
    {
        await using var file = Open(destinationPath, write: true);
        await this.Backup
            .ExportAsync(file, new BackupExportOptions(), ct)
            .ConfigureAwait(false);
    }

    public async Task<int> RestoreFromAsync(string sourcePath, CancellationToken ct = default)
    {
        var written = await IsLegacySqliteBackupAsync(sourcePath, ct).ConfigureAwait(false)
            ? await RestoreLegacySqliteAsync(sourcePath, ct).ConfigureAwait(false)
            : await this.RestoreDocumentBackupAsync(sourcePath, ct).ConfigureAwait(false);

        // Everything the restore just overwrote is also held in memory by a singleton that only
        // re-reads when it makes the edit itself, so without this the UI keeps showing the
        // pre-restore data until the app is restarted. Each Reload raises Changed, which is what
        // repaints the people/sync grids.
        await config.ReloadAsync(ct).ConfigureAwait(false);
        await persons.ReloadAsync(ct).ConfigureAwait(false);
        await sync.ReloadAsync(ct).ConfigureAwait(false);
        await notificationPrefs.ReloadAsync(ct).ConfigureAwait(false);

        // Restored tokens may differ from any cached clients.
        factory.InvalidateAll();
        return written;
    }

    async Task<int> RestoreDocumentBackupAsync(string sourcePath, CancellationToken ct)
    {
        await using var file = Open(sourcePath, write: false);
        var result = await this.Backup
            .RestoreAsync(
                file,
                new BulkRestoreOptions
                {
                    // Merge into whatever is already here rather than demanding an empty store:
                    // restoring onto a machine that has been used is the normal case, and the
                    // default (Insert) fails the chunk on the first id already present. Replace
                    // also keeps the old SQLite path's INSERT OR REPLACE semantics — the backup
                    // wins per document, and documents it doesn't mention are left alone.
                    Mode = BulkWriteMode.Replace
                },
                ct
            )
            .ConfigureAwait(false);

        return (int)result.DocumentsWritten;
    }

    static FileStream Open(string path, bool write) => new(
        path,
        write ? FileMode.Create : FileMode.Open,
        write ? FileAccess.Write : FileAccess.Read,
        write ? FileShare.None : FileShare.Read,
        bufferSize: 64 * 1024,
        useAsync: true
    );

    /// <summary>
    /// True when the file is a SQLite database — i.e. a backup taken before this moved to the
    /// store's own export format. Detected by the file's magic header rather than its extension,
    /// because the mobile document picker hands back a sandbox copy whose name is not ours.
    /// </summary>
    static async Task<bool> IsLegacySqliteBackupAsync(string path, CancellationToken ct)
    {
        // Every SQLite database file starts "SQLite format 3\0"; the trailing NUL is left off here
        // so the comparison is over printable bytes only.
        var magic = "SQLite format 3"u8.ToArray();
        var header = new byte[magic.Length];

        await using var file = Open(path, write: false);
        var read = await file
            .ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        return read == header.Length && header.AsSpan().SequenceEqual(magic);
    }

    /// <summary>
    /// Reads a pre-<see cref="IDocumentBackup"/> backup: a whole SQLite database file produced by
    /// <c>VACUUM INTO</c>. Copies every document table straight across on the live connection,
    /// which is safe because the file is the same app's own database, table for table.
    /// </summary>
    /// <remarks>
    /// Kept only so backups taken by earlier versions still restore — nothing writes this format
    /// any more. Delete it once those backups are old enough not to matter.
    /// </remarks>
    static async Task<int> RestoreLegacySqliteAsync(string sourcePath, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={AppPaths.DatabasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $src AS backup";
            attach.Parameters.AddWithValue("$src", sourcePath);
            await attach.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var rows = 0;
        try
        {
            // Every table in the file is copied. The database belongs exclusively to the
            // DocumentStore (see ConfigureModel in MauiProgram), so "every table" is exactly the
            // app's own data — the allow-list this replaced had gone stale and silently dropped
            // FollowedPerson, SyncMapping, AutoSyncPrefs, ClonePrefs and PollState.
            var backupTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using (var query = conn.CreateCommand())
            {
                query.CommandText =
                    """
                    SELECT name, sql FROM backup.sqlite_master
                    WHERE type = 'table' AND name NOT LIKE 'sqlite\_%' ESCAPE '\'
                    """;
                await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    backupTables[reader.GetString(0)] = reader.GetString(1);
            }

            var mainTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var query = conn.CreateCommand())
            {
                query.CommandText = "SELECT name FROM main.sqlite_master WHERE type = 'table'";
                await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    mainTables.Add(reader.GetString(0));
            }

            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var (table, ddl) in backupTables)
            {
                // A table the live store hasn't touched yet won't exist — create it
                // from the backup's own DDL (same app, same schema).
                if (!mainTables.Contains(table))
                {
                    await using var create = conn.CreateCommand();
                    create.Transaction = (SqliteTransaction)tx;
                    create.CommandText = ddl;
                    await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Name the columns rather than SELECT *: a backup taken before (or after) a
                // column was added still restores its shared columns instead of failing the
                // whole transaction on a count mismatch.
                var columns = await SharedColumnsAsync(conn, (SqliteTransaction)tx, table, ct).ConfigureAwait(false);
                if (columns.Count == 0)
                    continue;

                var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));
                await using var copy = conn.CreateCommand();
                copy.Transaction = (SqliteTransaction)tx;
                copy.CommandText =
                    $"INSERT OR REPLACE INTO main.\"{table}\" ({columnList}) SELECT {columnList} FROM backup.\"{table}\"";
                rows += await copy.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await using var detach = conn.CreateCommand();
            detach.CommandText = "DETACH DATABASE backup";
            await detach.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return rows;
    }

    /// <summary>
    /// The columns <paramref name="table"/> has in both databases, in the live table's order.
    /// </summary>
    static async Task<IReadOnlyList<string>> SharedColumnsAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        CancellationToken ct
    )
    {
        var backup = await ColumnsAsync("backup").ConfigureAwait(false);
        var main = await ColumnsAsync("main").ConfigureAwait(false);
        var inBackup = new HashSet<string>(backup, StringComparer.OrdinalIgnoreCase);
        return main.Where(inBackup.Contains).ToList();

        async Task<List<string>> ColumnsAsync(string schema)
        {
            var names = new List<string>();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // pragma_table_info is a table-valued function, so the table name is a bound value
            // here rather than an interpolated identifier.
            cmd.CommandText = $"SELECT name FROM {schema}.pragma_table_info($table)";
            cmd.Parameters.AddWithValue("$table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                names.Add(reader.GetString(0));
            return names;
        }
    }
}
