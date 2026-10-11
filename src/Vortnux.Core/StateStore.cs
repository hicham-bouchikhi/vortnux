using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Vortnux.Core;

public sealed class StateStore
{
    public string DirectoryPath { get; }
    public string DatabasePath { get; }

    public StateStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "vortnux.sqlite3");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public StoreState Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM state WHERE id=1";
        var payload = command.ExecuteScalar() as string;
        var state = payload is null ? new StoreState() :
            JsonSerializer.Deserialize(payload, AppJson.Default.StoreState)
            ?? throw new ModManagerException("The application database is unreadable.");
        if (state.Schema != 1) throw new ModManagerException("This application version cannot open this database schema.");
        return state;
    }

    public void Save(StoreState state)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO state(id,payload) VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state, AppJson.Default.StoreState));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Backup(string destination)
    {
        using var source = Open();
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        backup.Open();
        source.BackupDatabase(backup);
    }

    public void RestoreBackup(string sourcePath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        source.Open(); using var destination = Open(); source.BackupDatabase(destination);
    }

    public FileStream AcquireLock() => new(Path.Combine(DirectoryPath, "operation.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
