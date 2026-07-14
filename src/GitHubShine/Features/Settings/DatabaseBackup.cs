using Microsoft.Data.Sqlite;
using Shiny;

namespace GitHubShine.Settings;

public interface IDatabaseBackup
{
    /// <summary>Writes a clean snapshot of the live database (accounts, repos, tokens, card order, seen-state).</summary>
    Task BackupToAsync(string destinationPath, CancellationToken ct = default);

    /// <summary>Merges a backup file into the live database (insert-or-replace per row), then reloads config.</summary>
    Task<int> RestoreFromAsync(string sourcePath, CancellationToken ct = default);
}

[Singleton]
public sealed class SqliteDatabaseBackup(IConfigStore config, IGitProviderFactory factory) : IDatabaseBackup
{
    // Tables owned by the DocumentStore (see MapTypeToTable calls in MauiProgram).
    static readonly string[] Tables = ["MonitoredAccount", "StoredToken", "SeenFailedRun", "SeenInboxItem", "DashboardPrefs"];

    public async Task BackupToAsync(string destinationPath, CancellationToken ct = default)
    {
        // VACUUM INTO produces a compact, consistent single-file snapshot and is
        // safe while the app's own connections are open. It refuses to overwrite.
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        await using var conn = new SqliteConnection($"Data Source={AppPaths.DatabasePath}");
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", destinationPath);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> RestoreFromAsync(string sourcePath, CancellationToken ct = default)
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
            // Copy only tables present in BOTH databases — tolerates older backups
            // and runs against the live connection, so no file swapping or pool
            // juggling is needed.
            var backupTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using (var query = conn.CreateCommand())
            {
                query.CommandText = "SELECT name, sql FROM backup.sqlite_master WHERE type = 'table'";
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
            foreach (var table in Tables.Where(backupTables.ContainsKey))
            {
                // A table the live store hasn't touched yet won't exist — create it
                // from the backup's own DDL (same app, same schema).
                if (!mainTables.Contains(table))
                {
                    await using var create = conn.CreateCommand();
                    create.Transaction = (SqliteTransaction)tx;
                    create.CommandText = backupTables[table];
                    await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using var copy = conn.CreateCommand();
                copy.Transaction = (SqliteTransaction)tx;
                copy.CommandText = $"INSERT OR REPLACE INTO main.\"{table}\" SELECT * FROM backup.\"{table}\"";
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

        // Restored tokens may differ from any cached clients.
        await config.ReloadAsync(ct).ConfigureAwait(false);
        factory.InvalidateAll();
        return rows;
    }
}
