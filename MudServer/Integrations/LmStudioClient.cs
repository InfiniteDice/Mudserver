using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MudServer.Integrations
{
    /// <summary>
    /// Minimal LM Studio HTTP client wrapper for local/intranet LM Studio servers.
    /// - Uses JSON payloads against /v1/models, /v1/completions, /v1/chat/completions, /v1/embeddings
    /// - Returns parsed JsonDocument for callers to inspect.
    /// </summary>
    public sealed class LmStudioClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        private bool _disposed;

        public LmStudioClient(string baseUrl) : this(new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(baseUrl)) }) { }

        public LmStudioClient(HttpClient httpClient)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (!_http.DefaultRequestHeaders.Accept.Contains(new MediaTypeWithQualityHeaderValue("application/json")))
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // LM Studio usually runs locally and does not require auth by default.
            // If you bind auth later, set _http.DefaultRequestHeaders.Authorization appropriately.
        }

        public async Task<JsonDocument> GetModelsAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync("v1/models", ct).ConfigureAwait(false);
            var txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GetModels failed: {resp.StatusCode}: {txt}");
            return JsonDocument.Parse(txt);
        }

        public async Task<JsonDocument> CreateCompletionAsync(string model, string prompt,
            int? maxTokens = null, double? temperature = null, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                // LM Studio / Open-style endpoints often accept either "input" or "prompt".
                // Provide "input" (preferred by some local forks) and "prompt" for compatibility.
                ["input"] = prompt,
                ["prompt"] = prompt,
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature
            };

            return await PostJsonAsync("v1/completions", payload, ct).ConfigureAwait(false);
        }

        public async Task<JsonDocument> CreateChatCompletionAsync(string model, IEnumerable<(string role, string content)> messages,
            double? temperature = null, int? topP = null, CancellationToken ct = default)
        {
            var msgList = new List<Dictionary<string, string>>();
            foreach (var (role, content) in messages)
                msgList.Add(new Dictionary<string, string> { ["role"] = role, ["content"] = content });

            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = msgList,
                ["temperature"] = temperature,
                ["top_p"] = topP
            };

            return await PostJsonAsync("v1/chat/completions", payload, ct).ConfigureAwait(false);
        }

        public async Task<JsonDocument> CreateEmbeddingAsync(string model, string input, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["input"] = input
            };

            return await PostJsonAsync("v1/embeddings", payload, ct).ConfigureAwait(false);
        }

        private async Task<JsonDocument> PostJsonAsync(string path, object payload, CancellationToken ct)
        {
            var content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(path.TrimStart('/'), content, ct).ConfigureAwait(false);
            var txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"POST {path} failed: {resp.StatusCode}: {txt}");
            return JsonDocument.Parse(txt);
        }

        private static string EnsureTrailingSlash(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("baseUrl required", nameof(url));
            return url.EndsWith("/") ? url : url + "/";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _http?.Dispose();
            _disposed = true;
        }
    }
}