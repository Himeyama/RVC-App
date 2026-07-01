using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RvcRealtimeGui.Models;

namespace RvcRealtimeGui.Services;

public class RvcApiClient : IDisposable
{
    readonly HttpClient _http;
    readonly string _baseUrl;
    readonly Uri _wsUri;

    public RvcApiClient(string baseUrl = "http://127.0.0.1:6242")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _wsUri = new Uri(_baseUrl.Replace("http://", "ws://") + "/metrics");
    }

    public async Task<List<string>> GetHostApisAsync(CancellationToken ct = default)
    {
        List<string>? r = await _http.GetFromJsonAsync<List<string>>($"{_baseUrl}/hostapis", ct)
            .ConfigureAwait(false);
        return r ?? [];
    }

    public async Task<DevicesResponse> GetDevicesAsync(string? hostapi, CancellationToken ct = default)
    {
        string url = string.IsNullOrEmpty(hostapi)
            ? $"{_baseUrl}/devices"
            : $"{_baseUrl}/devices?hostapi={Uri.EscapeDataString(hostapi)}";
        DevicesResponse? r = await _http.GetFromJsonAsync<DevicesResponse>(url, ct).ConfigureAwait(false);
        return r ?? new DevicesResponse();
    }

    public async Task<RvcConfig> GetConfigAsync(CancellationToken ct = default)
    {
        RvcConfig? r = await _http.GetFromJsonAsync<RvcConfig>($"{_baseUrl}/config", ct).ConfigureAwait(false);
        return r ?? new RvcConfig();
    }

    public async Task SaveConfigAsync(RvcConfig cfg, CancellationToken ct = default)
    {
        HttpResponseMessage res = await _http.PostAsJsonAsync($"{_baseUrl}/config", cfg, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
    }

    public async Task<StartResponse> StartAsync(RvcConfig cfg, CancellationToken ct = default)
    {
        HttpResponseMessage res = await _http.PostAsJsonAsync($"{_baseUrl}/start", cfg, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"{(int)res.StatusCode}: {body}");
        }
        StartResponse? r = await res.Content.ReadFromJsonAsync<StartResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        return r ?? new StartResponse();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        HttpResponseMessage res = await _http.PostAsync($"{_baseUrl}/stop", null, ct).ConfigureAwait(false);
        // 既に停止していても警告レベル: 例外にしない
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            HttpResponseMessage res = await _http.GetAsync($"{_baseUrl}/status", ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task ListenMetricsAsync(Action<MetricsPayload> onMetric, CancellationToken ct)
    {
        using ClientWebSocket ws = new();
        await ws.ConnectAsync(_wsUri, ct).ConfigureAwait(false);
        byte[] buffer = new byte[1024];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;
            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            MetricsPayload? metric = JsonSerializer.Deserialize<MetricsPayload>(json);
            if (metric is not null) onMetric(metric);
        }
    }

    // ── 学習系 ────────────────────────────────────────────────

    public async Task<string> StartPreprocessAsync(PreprocessRequest req, CancellationToken ct = default) =>
        await PostForJobIdAsync("/train/preprocess", req, ct).ConfigureAwait(false);

    public async Task<string> StartExtractF0FeatureAsync(ExtractF0FeatureRequest req, CancellationToken ct = default) =>
        await PostForJobIdAsync("/train/extract_f0_feature", req, ct).ConfigureAwait(false);

    public async Task<string> StartTrainAsync(TrainRequest req, CancellationToken ct = default) =>
        await PostForJobIdAsync("/train/start", req, ct).ConfigureAwait(false);

    public async Task<string> StartTrainIndexAsync(TrainIndexRequest req, CancellationToken ct = default) =>
        await PostForJobIdAsync("/train/index", req, ct).ConfigureAwait(false);

    public async Task<string> StartOneClickTrainAsync(Train1KeyRequest req, CancellationToken ct = default) =>
        await PostForJobIdAsync("/train/one_click", req, ct).ConfigureAwait(false);

    public async Task<JobStatusResponse> GetTrainingJobAsync(string jobId, CancellationToken ct = default)
    {
        JobStatusResponse? r = await _http
            .GetFromJsonAsync<JobStatusResponse>($"{_baseUrl}/train/jobs/{Uri.EscapeDataString(jobId)}", ct)
            .ConfigureAwait(false);
        return r ?? new JobStatusResponse();
    }

    async Task<string> PostForJobIdAsync<T>(string path, T body, CancellationToken ct)
    {
        HttpResponseMessage res = await _http.PostAsJsonAsync($"{_baseUrl}{path}", body, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            string errBody = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"{(int)res.StatusCode}: {errBody}");
        }
        JobStartResponse? r = await res.Content.ReadFromJsonAsync<JobStartResponse>(cancellationToken: ct)
            .ConfigureAwait(false);
        return r?.JobId ?? "";
    }

    public void Dispose() => _http.Dispose();
}
