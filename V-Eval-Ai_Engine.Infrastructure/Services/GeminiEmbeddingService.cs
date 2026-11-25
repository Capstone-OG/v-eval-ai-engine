using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using V_Eval_Ai_Engine.Application.Interfaces;

namespace V_Eval_Ai_Engine.Infrastructure.Services;

/// <summary>
/// Dịch vụ nhúng vector 768 chiều dùng Gemini Embedding API (models/gemini-embedding-001 với MRL outputDimensionality = 768)
/// Hỗ trợ xoay vòng nhiều API Key (Multi-Key Rotation), Batch Embedding và L2-normalization cho pgvector HNSW.
/// </summary>
public class GeminiEmbeddingService : IEmbeddingService
{
    public const int TargetDimensions = 768;
    private const string ModelName = "models/gemini-embedding-001";
    private const int MaxBatchSize = 15;
    private const int MaxInputChars = 8000;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GeminiEmbeddingService> _logger;
    private readonly List<string> _apiKeys;
    private int _keyCursor;

    public GeminiEmbeddingService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<GeminiEmbeddingService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKeys = LoadApiKeys(configuration);
    }

    private static List<string> LoadApiKeys(IConfiguration configuration)
    {
        var keys = new List<string>();
        var listA = configuration.GetSection("AiSettings:GeminiApiKeys").Get<List<string>>();
        var listB = configuration.GetSection("Gemini:ApiKeys").Get<List<string>>();
        var singleA = configuration["AiSettings:GeminiApiKey"];
        var singleB = configuration["Gemini:ApiKey"];
        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        foreach (var k in (listA ?? []).Concat(listB ?? []))
        {
            if (!string.IsNullOrWhiteSpace(k) && !keys.Contains(k.Trim()))
                keys.Add(k.Trim());
        }
        foreach (var single in new[] { singleA, singleB, envKey })
        {
            if (!string.IsNullOrWhiteSpace(single) && !keys.Contains(single.Trim()))
                keys.Add(single.Trim());
        }
        return keys;
    }

    private string? GetNextKey(int offset = 0)
    {
        if (_apiKeys.Count == 0) return null;
        var idx = (Interlocked.Increment(ref _keyCursor) + offset) % _apiKeys.Count;
        if (idx < 0) idx += _apiKeys.Count;
        return _apiKeys[idx];
    }

    public async Task<float[]?> EmbedTextAsync(
        string text,
        string taskType = "RETRIEVAL_QUERY",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || _apiKeys.Count == 0)
            return null;

        var trimmed = text.Length > MaxInputChars ? text[..MaxInputChars] : text;
        var payload = new
        {
            model = ModelName,
            content = new { parts = new[] { new { text = trimmed } } },
            taskType,
            outputDimensionality = TargetDimensions
        };
        var json = JsonSerializer.Serialize(payload);

        for (int attempt = 0; attempt < _apiKeys.Count; attempt++)
        {
            var key = GetNextKey(attempt);
            if (string.IsNullOrWhiteSpace(key)) break;

            var url = $"https://generativelanguage.googleapis.com/v1beta/{ModelName}:embedContent?key={Uri.EscapeDataString(key)}";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                using var resp = await _httpClient.SendAsync(req, cancellationToken);
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    _logger.LogWarning("Gemini EmbedText HTTP {Code} (attempt {Attempt}), rotating key...", (int)resp.StatusCode, attempt + 1);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Gemini EmbedText failed HTTP {Code}: {Body}", (int)resp.StatusCode, err.Length > 200 ? err[..200] : err);
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("embedding", out var emb) &&
                    emb.TryGetProperty("values", out var vals) &&
                    vals.ValueKind == JsonValueKind.Array)
                {
                    var vec = ParseAndNormalize(vals);
                    if (vec != null) return vec;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Gemini EmbedText exception on attempt {Attempt}", attempt + 1);
            }
        }

        return null;
    }

    public async Task<List<float[]?>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        string taskType = "RETRIEVAL_DOCUMENT",
        CancellationToken cancellationToken = default)
    {
        var results = new List<float[]?>(texts.Count);
        for (int i = 0; i < texts.Count; i++) results.Add(null);

        if (texts.Count == 0 || _apiKeys.Count == 0)
            return results;

        for (int start = 0; start < texts.Count; start += MaxBatchSize)
        {
            int count = Math.Min(MaxBatchSize, texts.Count - start);
            var batchRequests = new List<object>(count);
            for (int i = 0; i < count; i++)
            {
                var raw = texts[start + i] ?? string.Empty;
                var trimmed = raw.Length > MaxInputChars ? raw[..MaxInputChars] : raw;
                batchRequests.Add(new
                {
                    model = ModelName,
                    content = new { parts = new[] { new { text = string.IsNullOrWhiteSpace(trimmed) ? " " : trimmed } } },
                    taskType,
                    outputDimensionality = TargetDimensions
                });
            }

            var json = JsonSerializer.Serialize(new { requests = batchRequests });
            bool batchSucceeded = false;

            for (int attempt = 0; attempt < _apiKeys.Count; attempt++)
            {
                var key = GetNextKey(attempt);
                if (string.IsNullOrWhiteSpace(key)) break;

                var url = $"https://generativelanguage.googleapis.com/v1beta/{ModelName}:batchEmbedContents?key={Uri.EscapeDataString(key)}";
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    using var resp = await _httpClient.SendAsync(req, cancellationToken);
                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    {
                        _logger.LogWarning("Gemini BatchEmbed HTTP {Code} (attempt {Attempt}), rotating key...", (int)resp.StatusCode, attempt + 1);
                        await Task.Delay(350 * (attempt + 1), cancellationToken);
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogWarning("Gemini BatchEmbed failed HTTP {Code}: {Body}", (int)resp.StatusCode, err.Length > 200 ? err[..200] : err);
                        continue;
                    }

                    var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("embeddings", out var embs) && embs.ValueKind == JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var item in embs.EnumerateArray())
                        {
                            if (idx >= count) break;
                            if (item.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                            {
                                results[start + idx] = ParseAndNormalize(vals);
                            }
                            idx++;
                        }
                        batchSucceeded = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Gemini BatchEmbed exception on attempt {Attempt}", attempt + 1);
                }
            }

            if (!batchSucceeded)
            {
                _logger.LogWarning("Batch embedding failed for slice {Start}..{End}", start, start + count - 1);
            }
        }

        return results;
    }

    private static float[]? ParseAndNormalize(JsonElement vals)
    {
        int len = vals.GetArrayLength();
        if (len != TargetDimensions) return null;

        var vec = new float[len];
        double normSq = 0;
        int i = 0;
        foreach (var el in vals.EnumerateArray())
        {
            float v = el.GetSingle();
            vec[i++] = v;
            normSq += (double)v * v;
        }

        if (normSq > 1e-12)
        {
            float invNorm = (float)(1.0 / Math.Sqrt(normSq));
            for (int k = 0; k < len; k++) vec[k] *= invNorm;
        }
        return vec;
    }

    public static string ToPgVectorLiteral(float[] embedding)
    {
        var sb = new StringBuilder(embedding.Length * 11 + 2);
        sb.Append('[');
        for (int i = 0; i < embedding.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(embedding[i].ToString("G7", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
