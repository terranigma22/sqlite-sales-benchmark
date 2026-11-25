using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sales.Api.Data;

/// <summary>
/// PRAGMAs por conexión. journal_mode es persistente en el fichero y se aplica
/// aparte, al crear/reiniciar la base; el resto se aplica en cada conexión.
/// </summary>
public sealed class SqlitePragmaInterceptor(SqliteSettings settings) : DbConnectionInterceptor
{
    private string Pragmas => $"""
        PRAGMA synchronous={settings.Synchronous};
        PRAGMA busy_timeout={settings.BusyTimeoutMs};
        PRAGMA cache_size=-{settings.CacheSizeKb};
        PRAGMA temp_store=MEMORY;
        PRAGMA fullfsync={(settings.FullFsync ? "ON" : "OFF")};
        PRAGMA checkpoint_fullfsync={(settings.CheckpointFullFsync ? "ON" : "OFF")};
        """;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }
}
