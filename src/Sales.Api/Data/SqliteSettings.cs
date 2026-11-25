using Sales.Contracts;

namespace Sales.Api.Data;

/// <summary>
/// Configuración SQLite de la corrida. Se lee de variables de entorno para poder
/// comparar PRAGMAs sin tocar código:
///   SQLITE_JOURNAL_MODE=WAL|DELETE
///   SQLITE_SYNCHRONOUS=OFF|NORMAL|FULL|EXTRA
///   SQLITE_FULLFSYNC=0|1
///   SQLITE_CHECKPOINT_FULLFSYNC=0|1
/// </summary>
public sealed class SqliteSettings
{
    public required string JournalMode { get; init; }
    public required string Synchronous { get; init; }
    public bool FullFsync { get; init; }
    public bool CheckpointFullFsync { get; init; }
    public int CacheSizeKb { get; init; } = 65_536;
    public int BusyTimeoutMs { get; init; } = 5_000;

    public SqliteConfigDto ToDto()
        => new(JournalMode, Synchronous, FullFsync, CheckpointFullFsync, CacheSizeKb, BusyTimeoutMs);

    /// <summary>SQL constante por modo: evita interpolar valores en ExecuteSqlRaw.</summary>
    public string JournalPragma => JournalMode switch
    {
        "WAL" => "PRAGMA journal_mode=WAL;",
        "DELETE" => "PRAGMA journal_mode=DELETE;",
        "TRUNCATE" => "PRAGMA journal_mode=TRUNCATE;",
        "PERSIST" => "PRAGMA journal_mode=PERSIST;",
        "MEMORY" => "PRAGMA journal_mode=MEMORY;",
        _ => throw new InvalidOperationException($"journal_mode no soportado: {JournalMode}")
    };

    public static SqliteSettings FromConfiguration(IConfiguration configuration)
    {
        var journal = (configuration["SQLITE_JOURNAL_MODE"]
                       ?? configuration["Sqlite:JournalMode"]
                       ?? "WAL").Trim().ToUpperInvariant();
        var synchronous = (configuration["SQLITE_SYNCHRONOUS"]
                           ?? configuration["Sqlite:Synchronous"]
                           ?? "NORMAL").Trim().ToUpperInvariant();

        var journals = new HashSet<string>(StringComparer.Ordinal)
        {
            "WAL", "DELETE", "TRUNCATE", "PERSIST", "MEMORY"
        };
        if (!journals.Contains(journal))
            throw new InvalidOperationException(
                $"SQLITE_JOURNAL_MODE inválido: '{journal}'. Usa {string.Join(", ", journals)}.");

        var synchronousValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "OFF", "NORMAL", "FULL", "EXTRA"
        };
        if (!synchronousValues.Contains(synchronous))
            throw new InvalidOperationException(
                $"SQLITE_SYNCHRONOUS inválido: '{synchronous}'. Usa {string.Join(", ", synchronousValues)}.");

        return new SqliteSettings
        {
            JournalMode = journal,
            Synchronous = synchronous,
            FullFsync = ParseBool(configuration["SQLITE_FULLFSYNC"] ?? configuration["Sqlite:FullFsync"]),
            CheckpointFullFsync = ParseBool(
                configuration["SQLITE_CHECKPOINT_FULLFSYNC"] ?? configuration["Sqlite:CheckpointFullFsync"]),
            CacheSizeKb = configuration.GetValue("SQLITE_CACHE_KB", 65_536),
            BusyTimeoutMs = configuration.GetValue("SQLITE_BUSY_TIMEOUT_MS", 5_000)
        };
    }

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("on", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
