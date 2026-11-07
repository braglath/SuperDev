using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SuperDev.Services.Activity;

/// <summary>
/// Local activity database beside the project index. No account and no server.
/// </summary>
public sealed class ActivityDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public ActivityDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        DatabasePath = databasePath;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        EnsureSchema();
    }

    public string DatabasePath { get; }

    public static string DefaultPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Super Dev");
        return Path.Combine(folder, "activity.db");
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            work(_connection, transaction);
            transaction.Commit();
        }
    }

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
            return work(_connection);
    }

    public IReadOnlyList<string> IndexNames()
    {
        return Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name
                FROM sqlite_master
                WHERE type = 'index' AND name LIKE 'IX_%'
                ORDER BY name
                """;
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _connection.Dispose();
    }

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ActivityEvents (
                Id TEXT PRIMARY KEY,
                TasklistId TEXT NOT NULL,
                ProjectId TEXT NULL,
                ChecklistItemId TEXT NULL,
                EventType TEXT NOT NULL,
                EventTimestamp TEXT NOT NULL,
                LocalDate TEXT NOT NULL,
                Metadata TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS TasklistSnapshots (
                TasklistId TEXT PRIMARY KEY,
                ProjectId TEXT NULL,
                Title TEXT NOT NULL,
                WorkType TEXT NOT NULL,
                Framework TEXT NULL,
                FilePath TEXT NULL,
                Status TEXT NOT NULL,
                PendingChecks INTEGER NOT NULL,
                CompletedChecks INTEGER NOT NULL,
                FirstSeenAt TEXT NOT NULL,
                DeletedAt TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS CheckSnapshots (
                TasklistId TEXT NOT NULL,
                ChecklistItemId TEXT NOT NULL,
                IsCompleted INTEGER NOT NULL,
                PRIMARY KEY (TasklistId, ChecklistItemId)
            );

            CREATE INDEX IF NOT EXISTS IX_ActivityEvents_EventTimestamp ON ActivityEvents(EventTimestamp);
            CREATE INDEX IF NOT EXISTS IX_ActivityEvents_TasklistId ON ActivityEvents(TasklistId);
            CREATE INDEX IF NOT EXISTS IX_ActivityEvents_ProjectId ON ActivityEvents(ProjectId);
            CREATE INDEX IF NOT EXISTS IX_ActivityEvents_EventType ON ActivityEvents(EventType);
            CREATE INDEX IF NOT EXISTS IX_TasklistSnapshots_ProjectId ON TasklistSnapshots(ProjectId);

            INSERT INTO SchemaVersion (Id, Version)
            SELECT 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion WHERE Id = 1);
            """;
        command.ExecuteNonQuery();
    }
}
