// PERUBAHAN DARI VERSI WINFORMS:
// Konstruktor sekarang menerima IConfiguration via DI
// (ganti ConfigurationBuilder manual yang pakai AppContext.BaseDirectory)
// Semua logic lainnya TIDAK BERUBAH sama sekali.
//
// PATCH (gpt-5.4-nano):
// - max_tokens -> max_completion_tokens (model GPT-5 menolak max_tokens)
// - temperature / top_p dihapus
// - api-version dinaikkan ke 2024-12-01-preview (lewat konstanta ApiVersion)
// - Prompt dirombak: satu kerangka ringkas, anti-halusinasi semua bahasa, term hint Jepang dinamis
// - Pasca-proses semantic-jp dipanggil untuk target Jepang; prompt retry memakai bahasa target yang benar

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
        private const string ApiVersion = "2024-12-01-preview";
        private const int MaxCompletionTokens = 1024;

        private readonly HttpClient httpClient;
        private readonly string endpoint;
        private readonly string apiKey;
        private readonly string deployment;
        private readonly string? reasoningEffort;

        private Dictionary<string, object> BuildRequest(string systemPrompt, string userText, bool stream = false)
        {
            var body = new Dictionary<string, object>
            {
                ["messages"] = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userText }
                },
                ["max_completion_tokens"] = MaxCompletionTokens
            };
            if (reasoningEffort != null) body["reasoning_effort"] = reasoningEffort;
            if (stream)                  body["stream"] = true;
            return body;
        }

        // Log ringkas untuk mengukur biaya/latensi nyata (reasoning tokens ditagih sebagai output).
        private static void LogUsage(JsonElement root, long elapsedMs)
        {
            try
            {
                string finish = "?";
                if (root.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0 &&
                    ch[0].TryGetProperty("finish_reason", out var fr))
                    finish = fr.GetString() ?? "?";

                int pt = 0, ct = 0, rt = 0;
                if (root.TryGetProperty("usage", out var u))
                {
                    if (u.TryGetProperty("prompt_tokens", out var p)) pt = p.GetInt32();
                    if (u.TryGetProperty("completion_tokens", out var c)) ct = c.GetInt32();
                    if (u.TryGetProperty("completion_tokens_details", out var d) &&
                        d.TryGetProperty("reasoning_tokens", out var r)) rt = r.GetInt32();
                }
                Console.WriteLine($"[AI-USAGE] ms={elapsedMs} in={pt} out={ct} reasoning={rt} finish={finish}");
            }
            catch { }
        }

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

            // Opsional: env AzureOpenAI__ReasoningEffort (mis. "minimal" / "low").
            // Kosong = tidak dikirim (aman). Nilai yang tidak didukung deployment -> HTTP 400,
            // jadi uji dulu dengan curl sebelum mengisi.
            var effort = config["AzureOpenAI:ReasoningEffort"];
            reasoningEffort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();

            httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout     = TimeSpan.FromSeconds(10) // was 30: panggilan macet menahan antrean caption committed
            };
            httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
            httpClient.DefaultRequestHeaders.Add("Accept-Charset", "utf-8");

            Console.WriteLine("[INIT] AI Translation engine ready");
        }

        private bool IsCasual(string text)
        {
            // Cocokkan per kata utuh (sebelumnya substring: "lo" cocok di "color", "ya" di "saya").
            var words = text.Split(
                new[] { ' ', '\t', '\r', '\n', ',', '.', '?', '!', ';', ':' },
                StringSplitOptions.RemoveEmptyEntries);
            return words.Count(w => casualIndicators.Contains(w)) >= 2;
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

            var requestBody = BuildRequest(GetSystemPrompt(sourceLang, targetLang, casual, text), text);

            try
            {
                var sw       = System.Diagnostics.Stopwatch.StartNew();
                var json     = JsonSerializer.Serialize(requestBody);
                var content  = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(
                    $"/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[ERROR] API {(int)response.StatusCode}: {errBody.Substring(0, Math.Min(300, errBody.Length))}");
                    return text;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc    = JsonDocument.Parse(responseJson);
                LogUsage(doc.RootElement, sw.ElapsedMilliseconds);

                var result = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()?.Trim() ?? text;

                if (IsRefusalResponse(result, text))
                {
                    Console.WriteLine($"[REFUSAL] Detected, retrying");
                    result = await RetryWithMinimalPromptAsync(text, targetLang);
                }

                if (IsJapanese(targetLang))
                    result = LiveTranslation.AI.BusinessMeetingContext.Process(text, result);

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
                var requestBody = BuildRequest(
                    GetSystemPrompt(sourceLang, targetLang, casual, text), text, stream: true);

                var json    = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}")
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

                var streamed = fullResult.ToString().Trim();
                if (IsJapanese(targetLang))
                    streamed = LiveTranslation.AI.BusinessMeetingContext.Process(text, streamed);

                var finalResult = NormalizeOrFallback(streamed, text, baseContextKey);
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

        // ── Prompt (dioptimalkan) ────────────────────────────────────────────────
        // Satu kerangka ringkas untuk SEMUA bahasa + satu baris gaya per bahasa.
        // - Hemat token: ~100 token vs ~170-290 token sebelumnya (tergantung bahasa).
        // - Anti-halusinasi untuk SEMUA bahasa: versi lama tidak punya aturan ini di 9 bahasa
        //   (ms/th/vi/fil/km/my/lo/ta/ko) dan malah menyuruh "reconstruct ONE clean sentence".
        // - Aturan khusus hanya muncul bila relevan (katakana hanya bila sumber Jepang, register
        //   Indonesia informal hanya bila sumber Indonesia, term hint Jepang hanya bila ada istilahnya).
        private static string LangKey(string? code)
        {
            var c = (code ?? "").Trim().ToLowerInvariant();
            var dash = c.IndexOf('-');
            if (dash > 0) c = c.Substring(0, dash);
            return c == "jp" ? "ja" : c;
        }

        private static string LangName(string? code) => LangKey(code) switch
        {
            "en" => "English",   "id" => "Indonesian", "ja" => "Japanese",
            // [FUTURE-LANG] Bahasa lain di-comment sementara (hanya id/en/ja yang aktif).
            // Un-comment baris terkait untuk mengaktifkan lagi (lihat NOTICE di TranslationDefaults.cs).
            // "ko" => "Korean",
            // "ms" => "Malay",     "th" => "Thai",       "vi" => "Vietnamese", "fil" => "Filipino",
            // "km" => "Khmer",     "my" => "Burmese",    "lo" => "Lao",      "ta" => "Tamil",
            _ => "English"
        };

        private static bool IsJapanese(string? code) => LangKey(code) == "ja";

        private static string LangStyle(string? code, bool casual) => LangKey(code) switch
        {
            "en"  => "Natural spoken English; contractions are fine.",
            "id"  => "Natural spoken Indonesian, clear and professional, never stiff.",
            "ja"  => casual
                ? "Casual spoken Japanese (友達口調), natural and relaxed. Drop subjects/pronouns when clear."
                : "Natural polite spoken Japanese (です・ます調) as a Japanese businessperson would say it: drop subjects/pronouns when clear, avoid 直訳調 and over-stiff keigo. Product/tech names stay in EN or katakana.",
            // [FUTURE-LANG] Bahasa lain di-comment sementara (hanya id/en/ja yang aktif).
            // Un-comment baris terkait untuk mengaktifkan lagi (lihat NOTICE di TranslationDefaults.cs).
            // "ms"  => "Natural formal Malaysian Malay (kami, anda, beliau); drop lah, kan, eh, lor.",
            // "th"  => "Natural polite spoken Thai (ครับ/ค่ะ) in Thai script, no romanization; omit pronouns where Thai would.",
            // "vi"  => "Natural professional spoken Vietnamese with correct diacritics, not textbook-stiff.",
            // "fil" => "Natural professional Filipino; EN code-switching is fine.",
            // "km"  => "Natural formal spoken Khmer in Khmer script.",
            // "my"  => "Natural formal spoken Burmese in Myanmar script.",
            // "lo"  => "Natural formal spoken Lao in Lao script.",
            // "ta"  => "Natural formal spoken Tamil (Malaysia/Singapore style) in Tamil script.",
            // "ko"  => "Natural polite spoken Korean (합니다/습니다) in Hangul only; drop subjects when clear.",
            _     => ""
        };

        private static string GetSystemPrompt(string sourceLang, string targetLang, bool casual, string sourceText = "")
        {
            var src = LangKey(sourceLang);
            var tgt = LangKey(targetLang);

            // [FUTURE-LANG] Filler Korea di-comment sementara (lihat NOTICE di TranslationDefaults.cs).
            var fillers = src == "ja" ? "えー, あの"
                        // : src == "ko" ? "음, 어"
                        : "um, uh, eh";

            var sb = new StringBuilder(480);
            sb.Append("You are a live interpreter for business meetings. Translate the user's message into ")
              .Append(LangName(targetLang))
              // Utamakan makna & kewajaran, bukan kata-per-kata (hasil "kaku" = terjemahan harfiah).
              .Append(", conveying the meaning the way a native speaker would naturally say it: idiomatic spoken style, ")
              .Append("not word-for-word and not like a translated document. ")
              .Append(LangStyle(targetLang, casual))
              // Anti-halusinasi tanpa membuat hasil harfiah: larang menambah FAKTA, bukan menata ulang kalimat.
              .Append(" Add no facts, names or numbers that are not in the source; if the input is cut off, leave it unfinished instead of guessing the ending.")
              .Append(" Drop fillers (").Append(fillers).Append(").")
              .Append(" Keep personal, place and brand names unchanged; EN tech terms (AI, API, Cloud, Server) stay as-is or use their common local form.");

            if (src == "ja")
                sb.Append(" Convert katakana loanwords to their original term.");

            // Register informal Indonesia hanya relevan bila sumbernya Indonesia (atau belum diketahui).
            if (src == "id" || src == "")
                sb.Append(" Informal Indonesian (nah, gitu, kok, sih, dong): use natural register, keep the meaning.");

            if (tgt == "ja")
                sb.Append(LiveTranslation.AI.BusinessMeetingContext.GetTermHints(sourceText));

            sb.Append(" Never refuse, ask questions or comment; if unclear, translate what is there naturally without guessing.")
              .Append(" Output ONLY the translation.");

            return sb.ToString();
        }

        // Deteksi penolakan/penjelasan model (bukan terjemahan). Versi lama terlalu luas:
        // frasa seperti "unclear", "tidak jelas", "i'm sorry" juga muncul di terjemahan yang SAH
        // (mis. sumber "Maaf, saya terlambat" -> "I'm sorry, I'm late"), sehingga memicu retry
        // (= panggilan API tambahan) dan bisa menimpa hasil yang benar dengan teks sumber.
        // Sekarang penanda hanya dihitung bila TIDAK ada di teks sumber.
        private static bool IsRefusalResponse(string result, string source)
        {
            if (string.IsNullOrWhiteSpace(result)) return false;
            var r = result.ToLowerInvariant();
            var s = (source ?? "").ToLowerInvariant();

            // 1) Penolakan eksplisit
            string[] refusal =
            {
                "i cannot translate", "cannot translate", "can't translate",
                "saya tidak dapat menerjemahkan", "翻訳できません", "번역할 수 없"
            };
            foreach (var m in refusal)
                if (r.Contains(m) && !s.Contains(m)) return true;

            // 2) Model balik bertanya / minta klarifikasi
            string[] clarify =
            {
                "could you clarify", "please clarify", "bisa tolong diperjelas", "tolong diperjelas",
                "mohon diperjelas", "ditulis ulang", "disampaikan ulang"
            };
            foreach (var m in clarify)
                if (r.Contains(m) && !s.Contains(m)) return true;

            // 3) Permintaan maaf: sah jika pembicara memang minta maaf
            string[] apology = { "i'm sorry", "maaf, saya tidak", "申し訳ありません" };
            string[] apologyInSource = { "maaf", "sorry", "apolog", "すみません", "申し訳", "ごめん", "죄송", "미안" };
            if (apology.Any(m => r.Contains(m)) && !apologyInSource.Any(m => s.Contains(m)))
                return true;

            // 4) Penanda lemah: hanya dianggap penolakan bila keluaran jauh lebih panjang
            //    dari sumber (indikasi penjelasan, bukan terjemahan)
            string[] weak = { "tidak jelas", "sulit dipahami", "not clear", "unclear", "hard to understand" };
            if (weak.Any(m => r.Contains(m) && !s.Contains(m)) &&
                result.Length > s.Length * 2 + 30)
                return true;

            return false;
        }

        private async Task<string> RetryWithMinimalPromptAsync(string originalText, string targetLang)
        {
            // Bug lama: cek bahasa hanya "jp"/"ja-JP" (kode "ja" tidak cocok) dan semua bahasa selain
            // id/ko jatuh ke "Translate to English". Sekarang memakai nama bahasa target yang benar.
            var minimalPrompt = $"Translate to natural spoken {LangName(targetLang)}, not word-for-word. Add nothing. Output ONLY the translation.";

            var requestBody = BuildRequest(minimalPrompt, originalText);

            try
            {
                var json     = JsonSerializer.Serialize(requestBody);
                var content  = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(
                    $"/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}",
                    content);
                if (!response.IsSuccessStatusCode) return originalText;

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc    = JsonDocument.Parse(responseJson);
                var result = doc.RootElement
                    .GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                    .GetString()?.Trim() ?? originalText;

                return IsRefusalResponse(result, originalText) ? originalText : result;
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

        // Terjemahan adalah fungsi murni dari (bahasa sumber, bahasa target, teks). Jenis konteks
        // ("meeting|active" vs "meeting|committed") TIDAK boleh membedakan cache: dulu teks yang baru
        // saja diterjemahkan sebagai partial diterjemahkan ULANG saat di-commit (biaya + latensi ganda).
        // Konteks tetap dipakai untuk fallback "last success" (BuildBaseContextKey), bukan untuk cache ini.
        private static string BuildCacheKey(string sourceLang, string targetLang, string contextKey, string text)
        {
            var idx   = contextKey.LastIndexOf('|');
            var scope = idx > 0 ? contextKey.Substring(0, idx) : contextKey;
            return $"{NormLangKey(sourceLang)}→{NormLangKey(targetLang)}|{scope}|{NormalizeCacheText(text)}";
        }

        private static string NormalizeCacheText(string text)
            => string.Join(" ",
                (text ?? "").Trim().TrimEnd('.', ',', ';', ':', '!', '?', '。', '、', '！', '？', '…').Split(new[] { ' ', '\t', '\r', '\n' },
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