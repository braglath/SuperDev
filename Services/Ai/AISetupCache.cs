using System;
using System.IO;
using System.Text.Json;
using SuperDev.Models;

namespace SuperDev.Services.Ai;

public interface IAISetupCache
{
    AISetupRecord? Read();

    void Write(AISetupRecord record);
}

public sealed class FileAISetupCache : IAISetupCache
{
    private readonly string _path;

    public FileAISetupCache(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Super Dev", "ai-setup.json")
            : path;
    }

    public AISetupRecord? Read()
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var record = JsonSerializer.Deserialize<AISetupRecord>(File.ReadAllText(_path));
            if (record is null || string.IsNullOrWhiteSpace(record.Model))
                return null;
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Write(AISetupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class MemoryAISetupCache : IAISetupCache
{
    public AISetupRecord? Record { get; private set; }

    public AISetupRecord? Read() => Record;

    public void Write(AISetupRecord record) => Record = record;
}
