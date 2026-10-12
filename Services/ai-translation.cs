// PERUBAHAN DARI VERSI WINFORMS:
// Konstruktor sekarang menerima IConfiguration via DI
// (ganti ConfigurationBuilder manual yang pakai AppContext.BaseDirectory)
// Semua logic lainnya TIDAK BERUBAH sama sekali.

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace LiveTranslation.AI
{
    public class AITranslationEngine
    {
        private readonly HttpClient httpClient;
        private readonly string endpoint;
        private readonly string apiKey;
        private readonly string deployment;

        private sealed class CacheEntry
        {
            public CacheEntry(string value, DateTime storedAtUtc)
            {
                Value = value;
                StoredAtUtc = storedAtUtc;
                LastAccessedUtc = storedAtUtc;
            }

            public string Value { get; }
            public DateTime StoredAtUtc { get; }
            public DateTime LastAccessedUtc { get; set; }
        }

        private static readonly Dictionary<string, CacheEntry> translationCache = new();
        private static readonly Dictionary<string, CacheEntry> lastSuccessByContext = new();
        private static readonly object CacheLock = new();
        private const int MaxCacheSize = 500;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan LastSuccessTtl = TimeSpan.FromMinutes(6);

        public event Action<string>? OnStreamChunk;

        private readonly HashSet<string> casualIndicators = new(StringComparer.OrdinalIgnoreCase)
        {
            "aku", "gue", "gw", "lu", "lo", "dong", "ya", "kok", "sih", "deh",
            "mau", "pengen", "want", "wanna", "gonna", "btw", "fyi",
            "kalian", "guys", "bro", "bray",
        };

        // ── Konstruktor: IConfiguration via DI (ganti ConfigurationBuilder manual) ──
        public AITranslationEngine(IConfiguration config)
        {
            endpoint   = config["AzureOpenAI:Endpoint"]
                ?? throw new Exception("AzureOpenAI:Endpoint missing in appsettings.json");
            apiKey     = config["AzureOpenAI:Key"]
                ?? throw new Exception("AzureOpenAI:Key missing in appsettings.json");
            deployment = config["AzureOpenAI:Deployment"]
                ?? throw new Exception("AzureOpenAI:Deployment missing in appsettings.json");

            httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout     = TimeSpan.FromSeconds(30)
            };
            httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
            httpClient.DefaultRequestHeaders.Add("Accept-Charset", "utf-8");

            Console.WriteLine("[INIT] AI Translation engine ready");
        }

        private bool IsCasual(string text)
        {
            var lower = text.ToLower();
            return casualIndicators.Count(w => lower.Contains(w)) >= 2;
        }

        private static string NormLangKey(string lang) => lang.ToLowerInvariant() switch
        {
            "jp" or "ja" or "ja-jp" => "ja",
            "id" or "id-id"         => "id",
            "en" or "en-us"         => "en",
            _                       => lang.ToLowerInvariant()
        };

        public async Task<string> TranslateAsync(
            string text,
            string sourceLang,
            string targetLang,
            string contextKey = "")
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var normalizedContext = NormalizeContextKey(contextKey);
            var cacheKey = BuildCacheKey(sourceLang, targetLang, normalizedContext, text);
            var baseContextKey = BuildBaseContextKey(sourceLang, targetLang, normalizedContext);

            if (TryGetCache(cacheKey, out var cached))
            {
                Console.WriteLine("[CACHE] Hit");
                return cached;
            }

            var casual = IsCasual(text);

            var requestBody = new
            {
                messages = new[]
                {
                    new { role = "system", content = GetSystemPrompt(sourceLang, targetLang, casual) },
                    new { role = "user",   content = text }
                },
                temperature = 0.2,
                max_tokens  = 250,
                top_p       = 0.85
            };

            try
            {
                var json     = JsonSerializer.Serialize(requestBody);
                var content  = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(
                    $"/openai/deployments/{deployment}/chat/completions?api-version=2024-08-01-preview",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[ERROR] API {(int)response.StatusCode}: {errBody.Substring(0, Math.Min(300, errBody.Length))}");
                    return text;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc    = JsonDocument.Parse(responseJson);

                var result = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()?.Trim() ?? text;

                if (IsRefusalResponse(result))
                {
                    Console.WriteLine($"[REFUSAL] Detected, retrying");
                    result = await RetryWithMinimalPromptAsync(text, targetLang);
                }

                var finalResult = NormalizeOrFallback(result, text, baseContextKey);
                if (!string.IsNullOrWhiteSpace(finalResult))
                    CacheStore(cacheKey, finalResult);
                return finalResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
                return NormalizeOrFallback(text, text, baseContextKey);
            }
        }

        public async Task<string> TranslateStreamAsync(
            string text,
            string sourceLang,
            string targetLang,
            string contextKey = "")
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            var normalizedContext = NormalizeContextKey(contextKey);
            var cacheKey = BuildCacheKey(sourceLang, targetLang, normalizedContext, text);
            var baseContextKey = BuildBaseContextKey(sourceLang, targetLang, normalizedContext);

            if (TryGetCache(cacheKey, out var cached))
            {
                OnStreamChunk?.Invoke(cached);
                return cached;
            }

            var casual = IsCasual(text);

            try
            {
                var requestBody = new
                {
                    messages = new[]
                    {
                        new { role = "system", content = GetSystemPrompt(sourceLang, targetLang, casual) },
                        new { role = "user",   content = text }
                    },
                    temperature = 0.2,
                    max_tokens  = 250,
                    top_p       = 0.85,
                    stream      = true
                };

                var json    = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"/openai/deployments/{deployment}/chat/completions?api-version=2024-08-01-preview")
                { Content = content };

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) return text;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new System.IO.StreamReader(stream);

                var fullResult = new StringBuilder();
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
                    var data = line.Substring(6);
                    if (data == "[DONE]") break;

                    try
                    {
                        using var chunkDoc = JsonDocument.Parse(data);
                        var delta = chunkDoc.RootElement
                            .GetProperty("choices")[0]
                            .GetProperty("delta");
                        if (delta.TryGetProperty("content", out var c))
                        {
                            var chunk = c.GetString() ?? "";
                            fullResult.Append(chunk);
                            OnStreamChunk?.Invoke(chunk);
                        }
                    }
                    catch { }
                }

                var finalResult = NormalizeOrFallback(fullResult.ToString().Trim(), text, baseContextKey);
                if (!string.IsNullOrEmpty(finalResult))
                    CacheStore(cacheKey, finalResult);

                return finalResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STREAM ERROR] {ex.Message}");
                return NormalizeOrFallback(text, text, baseContextKey);
            }
        }

        private string GetSystemPrompt(string sourceLang, string targetLang, bool casual)
        {
            var fromJP = sourceLang is "jp" or "ja" or "ja-JP";
            var toJP   = targetLang is "jp" or "ja" or "ja-JP";
            var toID   = targetLang is "id" or "id-ID";
            var toEN   = targetLang is "en" or "en-US";
            var toMS    = targetLang is "ms" or "ms-MY";
            var toTH    = targetLang is "th" or "th-TH";
            var toVI    = targetLang is "vi" or "vi-VN";
            var toFIL   = targetLang is "fil" or "fil-PH";
            var toKM    = targetLang is "km" or "km-KH";
            var toMY    = targetLang is "my" or "my-MM";
            var toLO    = targetLang is "lo" or "lo-LA";
            var toTA    = targetLang is "ta" or "ta-MY";
            var toKO    = targetLang is "ko" or "ko-KR";
            var fromKO  = sourceLang is "ko" or "ko-KR";

            var register        = casual ? "Register: casual spoken Japanese (友達口調)." : "Register: polite professional Japanese (丁寧語).";
            var noHallucination = " Do NOT add information not present in the source. ";
            var disfluencyRule  = " Strip filler words (um, uh, え～、あの～" + (fromKO ? ", 음, 어" : "") + ") unless they carry meaning. ";
            var informalIndoRule = " For informal Indonesian (nah, gitu, kok, sih, dong): clean up to natural spoken register while preserving meaning. ";
            var noRefusal       = " Never refuse to translate. If input is unclear, produce the best possible natural translation. ";
            var preserveNames   = " Preserve personal names, nicknames, place names, and brand names; do NOT translate or paraphrase them. ";
            var noClarify       = " Do NOT ask questions or mention the input is unclear; always translate as best as possible. ";
            var termHints       = LiveTranslation.AI.BusinessMeetingContext.GetTermHints();
            string BuildAseanPrompt(string languageName, string languageStyle, string extraRule) =>
                "You are a skilled live interpreter. " +
                $"Render speech into natural spoken {languageName}. " +
                $"Use clear, natural {languageStyle} as a professional speaker would say it — " +
                "NOT like a translated document, NOT overly stiff or academic. " +
                "Keep EN tech terms as-is (AI, API, Machine Learning, Cloud, Server, Data, System, Network, Database, dll). " +
                "For fragmented or mid-sentence input: reconstruct ONE clean natural sentence. " +
                "Strip filler words and disfluencies. " +
                extraRule + preserveNames + noClarify +
                "Never refuse to translate. " +
                "If input is unclear, produce the best possible natural translation. " +
                $"Output ONLY the {languageName} translation, nothing else.";

            if (toJP)
                return "You are a skilled live interpreter. " +
                       "Render speech into natural spoken Japanese (丁寧な会話体). " +
                        register + " " +
                        "Tech/product names stay in EN or katakana: AI, API, Machine Learning, IoT, Cloud, Server, Gmail, Zoom. " +
                        "For fragmented input: reconstruct ONE clean natural sentence. " +
                        noHallucination + disfluencyRule + informalIndoRule + preserveNames + noClarify + noRefusal + termHints +
                        " Output ONLY the Japanese translation, nothing else.";

            if (fromJP && toID)
                return "You are a skilled live interpreter. " +
                       "Render Japanese speech into natural spoken Indonesian. " +
                        "Use clear, natural Indonesian as a professional speaker would say it. " +
                        "Keep EN tech terms as-is. Convert obvious katakana to EN/Indonesian equivalent. " +
                        disfluencyRule + informalIndoRule + preserveNames + noClarify + noHallucination + noRefusal +
                        "Output ONLY the Indonesian translation, nothing else.";

            if (fromJP && toEN)
                return "You are a skilled live interpreter. " +
                       "Render Japanese speech into natural spoken English. " +
                        "Contractions are fine. Convert katakana to EN equivalent. " +
                        disfluencyRule + preserveNames + noClarify + noHallucination + noRefusal +
                        "Output ONLY the English translation, nothing else.";

            if (toID)
                return "You are a skilled live interpreter. " +
                       "Render speech into natural spoken Indonesian. " +
                        "EN tech terms stay as-is. " +
                        disfluencyRule + informalIndoRule + preserveNames + noClarify + noHallucination + noRefusal +
                        "Output ONLY the Indonesian translation, nothing else.";

            if (toEN)
                return "You are a skilled live interpreter. " +
                       "Render speech into natural spoken English. Contractions are fine. " +
                        disfluencyRule + informalIndoRule + preserveNames + noClarify + noHallucination + noRefusal +
                        "Output ONLY the English translation, nothing else.";

            if (toMS)
                return BuildAseanPrompt(
                    "Melayu",
                    "Melayu Malaysia formal",
                    "Use formal pronouns naturally: \"kami\" for we, \"anda\" for formal you, and \"beliau\" for formal he/she. Strip Malay filler words such as lah, kan, eh, lor. ");

            if (toTH)
                return BuildAseanPrompt(
                    "Thai",
                    "Thai",
                    "Use formal/polite Thai register (ครับ/ค่ะ). Output in Thai script, not romanization. Use common Thai transliteration for EN tech terms when natural, otherwise keep them in EN. ");

            if (toVI)
                return BuildAseanPrompt(
                    "Vietnamese",
                    "Vietnamese",
                    "Use formal/professional Vietnamese with correct diacritics. EN tech terms may stay EN or use common Vietnamese equivalents such as phần mềm (software), dữ liệu (data). ");

            if (toFIL)
                return BuildAseanPrompt(
                    "Filipino",
                    "Filipino",
                    "Use formal Filipino register. Professional Filipino code-switching with EN is acceptable and natural. ");

            if (toKM)
                return BuildAseanPrompt(
                    "Khmer",
                    "Khmer",
                    "Use formal Khmer and output in Khmer script. EN tech terms may stay EN or use Khmer transliteration when natural. ");

            if (toMY)
                return BuildAseanPrompt(
                    "Burmese",
                    "Burmese",
                    "Use formal Burmese and output in Myanmar script. Keep EN tech terms as-is when appropriate. ");

            if (toLO)
                return BuildAseanPrompt(
                    "Lao",
                    "Lao",
                    "Use formal Lao and output in Lao script. Keep EN tech terms as-is when appropriate. ");

            if (toTA)
                return BuildAseanPrompt(
                    "Tamil",
                    "Tamil Malaysia/Singapura formal",
                    "Use formal Tamil style suitable for Malaysia/Singapore and output in Tamil script. Keep EN tech terms as-is. ");

            if (toKO)
                return BuildAseanPrompt(
                    "Korean",
                    "Korean",
                    "Use polite professional Korean suitable for business meetings (합니다/습니다 style). Output in Hangul only. EN tech terms may stay EN or use common Korean loanwords (e.g. 클라우드, 서버, 데이터). ");

            return "You are a professional live interpreter. Translate naturally and accurately. " +
                   disfluencyRule + informalIndoRule + preserveNames + noClarify + noHallucination + noRefusal +
                   "Output ONLY the translation, nothing else.";
        }

        private static bool IsRefusalResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var normalized = text.ToLowerInvariant();
            return text.Contains("申し訳ありません", StringComparison.Ordinal) ||
                   text.Contains("翻訳できません", StringComparison.Ordinal) ||
                   text.Contains("번역할 수 없", StringComparison.Ordinal) ||
                   normalized.Contains("i'm sorry") || normalized.Contains("i cannot translate") ||
                   normalized.Contains("cannot translate") || normalized.Contains("can't translate") ||
                   normalized.Contains("maaf, saya tidak") || normalized.Contains("saya tidak dapat menerjemahkan") ||
                   normalized.Contains("tidak jelas") || normalized.Contains("sulit dipahami") ||
                   normalized.Contains("bisa tolong diperjelas") || normalized.Contains("tolong diperjelas") ||
                   normalized.Contains("mohon diperjelas") || normalized.Contains("ditulis ulang") ||
                   normalized.Contains("disampaikan ulang") || normalized.Contains("could you clarify") ||
                   normalized.Contains("please clarify") || normalized.Contains("not clear") ||
                   normalized.Contains("unclear") || normalized.Contains("hard to understand");
        }

        private async Task<string> RetryWithMinimalPromptAsync(string originalText, string targetLang)
        {
            var toJP = targetLang is "jp" or "ja-JP";
            var minimalPrompt = toJP
                ? "Translate to Japanese. Output ONLY the translation, nothing else."
                : targetLang is "id" or "id-ID"
                    ? "Translate to Indonesian. Output ONLY the translation, nothing else."
                    : targetLang is "ko" or "ko-KR"
                        ? "Translate to Korean. Output ONLY the translation, nothing else."
                        : "Translate to English. Output ONLY the translation, nothing else.";

            var requestBody = new
            {
                messages = new[]
                {
                    new { role = "system", content = minimalPrompt },
                    new { role = "user",   content = originalText }
                },
                temperature = 0.1, max_tokens = 250, top_p = 0.8
            };

            try
            {
                var json     = JsonSerializer.Serialize(requestBody);
                var content  = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(
                    $"/openai/deployments/{deployment}/chat/completions?api-version=2024-08-01-preview",
                    content);
                if (!response.IsSuccessStatusCode) return originalText;

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc    = JsonDocument.Parse(responseJson);
                var result = doc.RootElement
                    .GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                    .GetString()?.Trim() ?? originalText;

                return IsRefusalResponse(result) ? originalText : result;
            }
            catch { return originalText; }
        }

        private static string NormalizeContextKey(string contextKey)
        {
            if (string.IsNullOrWhiteSpace(contextKey)) return "global";
            var compact = string.Join("-",
                contextKey.Trim().Split(new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries));
            return compact.ToLowerInvariant();
        }

        private static string BuildBaseContextKey(string sourceLang, string targetLang, string contextKey)
            => $"{NormLangKey(sourceLang)}→{NormLangKey(targetLang)}|{contextKey}";

        private static string BuildCacheKey(string sourceLang, string targetLang, string contextKey, string text)
            => $"{BuildBaseContextKey(sourceLang, targetLang, contextKey)}|{NormalizeCacheText(text)}";

        private static string NormalizeCacheText(string text)
            => string.Join(" ",
                (text ?? "").Trim().Split(new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();

        private static bool TryGetCache(string key, out string value)
        {
            lock (CacheLock)
            {
                if (translationCache.TryGetValue(key, out var entry))
                {
                    if (DateTime.UtcNow - entry.StoredAtUtc > CacheTtl)
                    {
                        translationCache.Remove(key);
                        value = "";
                        return false;
                    }
                    entry.LastAccessedUtc = DateTime.UtcNow;
                    value = entry.Value;
                    return true;
                }
            }
            value = "";
            return false;
        }

        private static void CacheStore(string key, string value)
        {
            var now = DateTime.UtcNow;
            lock (CacheLock)
            {
                translationCache[key] = new CacheEntry(value, now);
                PruneCache_NoLock(now);
            }
        }

        private static void PruneCache_NoLock(DateTime nowUtc)
        {
            if (translationCache.Count == 0) return;

            var expired = translationCache
                .Where(kv => nowUtc - kv.Value.StoredAtUtc > CacheTtl)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in expired) translationCache.Remove(key);

            if (translationCache.Count <= MaxCacheSize) return;

            var overflow = translationCache.Count - MaxCacheSize;
            var toRemove = translationCache
                .OrderBy(kv => kv.Value.LastAccessedUtc)
                .Take(Math.Max(overflow, 1))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in toRemove) translationCache.Remove(key);
        }

        private static string NormalizeOrFallback(string candidate, string sourceText, string baseContextKey)
        {
            var trimmed = candidate?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(trimmed) || IsTooShortTranslation(trimmed, sourceText))
            {
                if (TryGetLastSuccess(baseContextKey, out var last))
                    return last;
                return sourceText.Trim();
            }

            StoreLastSuccess(baseContextKey, trimmed);
            return trimmed;
        }

        private static void StoreLastSuccess(string baseContextKey, string value)
        {
            var now = DateTime.UtcNow;
            lock (CacheLock)
            {
                lastSuccessByContext[baseContextKey] = new CacheEntry(value, now);
            }
        }

        private static bool TryGetLastSuccess(string baseContextKey, out string value)
        {
            lock (CacheLock)
            {
                if (lastSuccessByContext.TryGetValue(baseContextKey, out var entry))
                {
                    if (DateTime.UtcNow - entry.StoredAtUtc > LastSuccessTtl)
                    {
                        lastSuccessByContext.Remove(baseContextKey);
                        value = "";
                        return false;
                    }
                    value = entry.Value;
                    return true;
                }
            }
            value = "";
            return false;
        }

        private static bool IsTooShortTranslation(string candidate, string sourceText)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return true;

            var cand = candidate.Trim();
            var src = sourceText?.Trim() ?? "";
            if (cand.Length <= 1) return true;

            var nonLatin = ContainsNonLatinScript(cand) || ContainsNonLatinScript(src);
            if (nonLatin)
            {
                if (cand.Length <= 3 && src.Length >= 8) return true;
                return false;
            }

            var candWords = WordCount(cand);
            var srcWords = WordCount(src);
            if (candWords <= 1 && srcWords >= 4) return true;
            if (candWords <= 2 && srcWords >= 8) return true;
            return false;
        }

        private static int WordCount(string text) =>
            string.IsNullOrWhiteSpace(text) ? 0
            : text.Trim().Split(new[] { ' ', '\t', '\n' },
                StringSplitOptions.RemoveEmptyEntries).Length;

        private static bool ContainsNonLatinScript(string text)
        {
            foreach (var c in text)
            {
                if (c >= 0x3040 && c <= 0x309F) return true; // Hiragana
                if (c >= 0x30A0 && c <= 0x30FF) return true; // Katakana
                if (c >= 0x4E00 && c <= 0x9FFF) return true; // CJK Unified Ideographs
                if (c >= 0x1780 && c <= 0x17FF) return true; // Khmer
                if (c >= 0x1000 && c <= 0x109F) return true; // Myanmar
                if (c >= 0x0E80 && c <= 0x0EFF) return true; // Lao
                if (c >= 0x0E00 && c <= 0x0E7F) return true; // Thai
                if (c >= 0x0B80 && c <= 0x0BFF) return true; // Tamil
                if (c >= 0xAC00 && c <= 0xD7AF) return true; // Hangul Syllables
                if (c >= 0x1100 && c <= 0x11FF) return true; // Hangul Jamo
                if (c >= 0x3130 && c <= 0x318F) return true; // Hangul Compatibility Jamo
            }
            return false;
        }

        public void ClearCache()
        {
            lock (CacheLock)
            {
                translationCache.Clear();
                lastSuccessByContext.Clear();
            }
            Console.WriteLine("[CACHE] Cleared");
        }

        public int GetCacheSize()
        {
            lock (CacheLock) { return translationCache.Count; }
        }
    }
}
