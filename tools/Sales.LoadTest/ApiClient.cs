using System.Net.Http.Json;
using Sales.Contracts;

namespace Sales.LoadTest;

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ApiClient(string baseUrl, int timeoutSeconds)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 5_000,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        })
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    public HttpClient Http => _http;

    public async Task WaitForHealthAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await _http.GetAsync("/api/health", ct);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }

            await Task.Delay(250, ct);
        }

        throw new InvalidOperationException(
            $"La API no responde en {_http.BaseAddress}. ¿Está arrancada? {last?.Message}");
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
               ?? throw new InvalidOperationException($"Respuesta vacía de {path}");
    }

    public async Task<T> PostAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(path, content: null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
               ?? throw new InvalidOperationException($"Respuesta vacía de {path}");
    }

    public Task<StatsSnapshot> GetStatsAsync(CancellationToken ct = default)
        => GetAsync<StatsSnapshot>("/api/stats", ct);

    public Task<ApiInfoDto> GetInfoAsync(CancellationToken ct = default)
        => GetAsync<ApiInfoDto>("/api/info", ct);

    public Task<CatalogResponse> GetCatalogAsync(CancellationToken ct = default)
        => GetAsync<CatalogResponse>("/api/catalog", ct);

    public Task<ResetResponse> ResetAsync(CancellationToken ct = default)
        => PostAsync<ResetResponse>("/api/admin/reset", ct);

    public Task<VerifyResponse> VerifyAsync(CancellationToken ct = default)
        => GetAsync<VerifyResponse>("/api/admin/verify", ct);

    public async Task<List<long>> GetPersistedIdsAsync(int pageSize, CancellationToken ct = default)
    {
        var all = new List<long>();
        long after = 0;

        while (true)
        {
            var page = await GetAsync<List<long>>(
                $"/api/admin/persisted-ids?after={after}&limit={pageSize}", ct);

            if (page.Count == 0)
                break;

            all.AddRange(page);
            after = page[^1];

            if (page.Count < pageSize)
                break;
        }

        return all;
    }

    public void Dispose() => _http.Dispose();
}
