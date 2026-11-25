namespace Sales.Api.Data;

public sealed class DatabasePaths
{
    public DatabasePaths(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
    }

    public string DbPath { get; }
    public string WalPath => DbPath + "-wal";
    public string ShmPath => DbPath + "-shm";

    public string ConnectionString => $"Data Source={DbPath};Pooling=True;Default Timeout=30";

    public static long FileSize(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;
}
