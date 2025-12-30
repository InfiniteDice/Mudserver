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

        /// Generate a structured room Description and DistantDescription using the LM Studio chat API.
        /// Returns a tuple (Description, DistantDescription).
        /// Throws InvalidOperationException on any LM/network/parse error.
        /// </summary>
        public async Task<(string Description, string DistantDescription)> GenerateRoomDescriptionsAsync(
            string model,
            string systemPrompt,
            string userPayload,
            int maxDescriptionChars = 4000,
            int maxDistantChars = 300,
            double? temperature = 0.7,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("model required", nameof(model));
            if (string.IsNullOrWhiteSpace(systemPrompt)) throw new ArgumentException("systemPrompt required", nameof(systemPrompt));
            if (string.IsNullOrWhiteSpace(userPayload)) throw new ArgumentException("userPayload required", nameof(userPayload));

            // Build messages in the same format used by CreateChatCompletionAsync
            var messages = new List<(string role, string content)>
            {
                ("system", systemPrompt),
                ("user", userPayload)
            };

            // Call chat completion
            JsonDocument respDoc;
            try
            {
                respDoc = await CreateChatCompletionAsync(model, messages, temperature, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"LM chat completion failed: {ex.Message}", ex);
            }

            // Extract text/content from LM response
            string generated;
            try
            {
                generated = ExtractChatContent(respDoc);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to extract LM content: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(generated))
                throw new InvalidOperationException("LM returned empty content.");

            // Heuristic: find JSON object in output (first '{' .. last '}')
            string jsonText = generated;
            try
            {
                int firstIdx = jsonText.IndexOf('{');
                int lastIdx = jsonText.LastIndexOf('}');
                if (firstIdx >= 0 && lastIdx > firstIdx)
                    jsonText = jsonText.Substring(firstIdx, lastIdx - firstIdx + 1);

                using var jd = JsonDocument.Parse(jsonText);
                var root = jd.RootElement;

                string desc = "";
                string distant = "";

                if (root.TryGetProperty("Description", out var pDesc) && pDesc.ValueKind == JsonValueKind.String)
                    desc = pDesc.GetString() ?? "";
                else if (root.TryGetProperty("description", out var pDesc2) && pDesc2.ValueKind == JsonValueKind.String)
                    desc = pDesc2.GetString() ?? "";

                if (root.TryGetProperty("DistantDescription", out var pDist) && pDist.ValueKind == JsonValueKind.String)
                    distant = pDist.GetString() ?? "";
                else if (root.TryGetProperty("distantDescription", out var pDist2) && pDist2.ValueKind == JsonValueKind.String)
                    distant = pDist2.GetString() ?? "";

                desc = (desc ?? "").Trim();
                distant = (distant ?? "").Trim();

                if (string.IsNullOrWhiteSpace(desc))
                    throw new InvalidOperationException("LM JSON did not contain a non-empty 'Description' field.");

                if (desc.Length > maxDescriptionChars) desc = desc.Substring(0, maxDescriptionChars).Trim();
                if (distant.Length > maxDistantChars) distant = distant.Substring(0, maxDistantChars).Trim();

                return (desc, distant);
            }
            catch (JsonException jex)
            {
                throw new InvalidOperationException("Failed to parse JSON from LM output.", jex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to extract structured output from LM response: {ex.Message}", ex);
            }
        }

        // Helper to extract the primary content string from a chat/completion JsonDocument.
        // Compatible with several LM Studio response shapes: choices[].message.content, choices[].text,
        // output_text, output.
        private static string ExtractChatContent(JsonDocument respDoc)
        {
            if (respDoc == null) return "";

            try
            {
                var root = respDoc.RootElement;

                // choices[0].message.content
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        if (first.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                            return content.GetString() ?? "";

                        if (first.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                            return textProp.GetString() ?? "";
                    }
                }

                // output_text (some endpoints)
                if (root.TryGetProperty("output_text", out var outText) && outText.ValueKind == JsonValueKind.String)
                    return outText.GetString() ?? "";

                // output (array or string)
                if (root.TryGetProperty("output", out var outNode))
                {
                    if (outNode.ValueKind == JsonValueKind.String) return outNode.GetString() ?? "";
                    if (outNode.ValueKind == JsonValueKind.Array && outNode.GetArrayLength() > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var el in outNode.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String)
                            {
                                if (sb.Length > 0) sb.Append("\n");
                                sb.Append(el.GetString());
                            }
                        }
                        return sb.ToString();
                    }
                }

                // fallback: stringify root
                return root.ToString();
            }
            catch
            {
                return "";
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _http?.Dispose();
            _disposed = true;
        }
    }
}