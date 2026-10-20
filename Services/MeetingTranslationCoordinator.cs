using System.Collections.Concurrent;
using LiveTranslation.AI;
using LiveTranslation.Engine;
using Microsoft.AspNetCore.SignalR;
using Teams_Translation_App.Hubs;

namespace Teams_Translation_App.Services
{
    public class MeetingTranslationCoordinator : IDisposable
    {
        private const string DefaultInputLang  = TranslationDefaults.DefaultInputLanguage;
        private const string DefaultOutputLang = TranslationDefaults.DefaultOutputLanguage;
        private const string EmptyActiveText = "";

        private readonly LiveTranslationEngine _engine;
        private readonly AITranslationEngine _ai;
        private readonly IHubContext<TranslationHub> _hub;
        private readonly ILogger<MeetingTranslationCoordinator> _logger;
        private readonly ConcurrentDictionary<string, ConnectionPreference> _preferences = new();
        private readonly SemaphoreSlim _dispatchLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TranslationDispatchState> _committedHistory = new();
        // Terjemahan partial (baris aktif): maks. 1 panggilan OpenAI berjalan per (meeting, bahasa target).
        private readonly ConcurrentDictionary<string, ActiveTranslateState> _activeStates = new();
        // Naik setiap ada commit; hasil partial yang dimulai sebelum commit dibuang (mencegah teks hantu).
        private readonly ConcurrentDictionary<string, int> _commitEpochs = new();

        public MeetingTranslationCoordinator(
            LiveTranslationEngine engine,
            AITranslationEngine ai,
            IHubContext<TranslationHub> hub,
            ILogger<MeetingTranslationCoordinator> logger)
        {
            _engine = engine;
            _ai = ai;
            _hub = hub;
            _logger = logger;
            _engine.OnFinalOriginal += HandleSourceCommitted;
            _engine.OnActiveSource += HandleActiveSource;
            _engine.OnCommittedTranslations += HandleCommittedTranslations;
        }

        public int ConnectionCount => _preferences.Count;

        public Task JoinMeetingAsync(string connectionId, string meetingId)
        {
            var safeMeetingId = string.IsNullOrWhiteSpace(meetingId) ? "default" : meetingId.Trim();
            var now = DateTime.UtcNow;
            _preferences.AddOrUpdate(connectionId,
                _ => new ConnectionPreference
                {
                    MeetingId = safeMeetingId,
                    InputLang = DefaultInputLang,
                    OutputLang = DefaultOutputLang,
                    LastInputChangedUtc = now
                },
                (_, existing) =>
                {
                    existing.MeetingId = safeMeetingId;
                    existing.InputLang ??= DefaultInputLang;
                    existing.OutputLang ??= DefaultOutputLang;
                    existing.LastInputChangedUtc = now;
                    return existing;
                });

            return RecomputeMajorityInputAsync(safeMeetingId);
        }

        public Task LeaveMeetingAsync(string connectionId)
        {
            if (_preferences.TryRemove(connectionId, out var removed))
                return RecomputeMajorityInputAsync(removed.MeetingId);
            return Task.CompletedTask;
        }

        public Task SetInputLanguageAsync(string connectionId, string lang)
        {
            var safeLang = NormalizeLanguage(lang, DefaultInputLang);
            if (_preferences.TryGetValue(connectionId, out var pref))
            {
                pref.InputLang = safeLang;
                pref.LastInputChangedUtc = DateTime.UtcNow;
                return RecomputeMajorityInputAsync(pref.MeetingId);
            }
            return Task.CompletedTask;
        }

        public Task SetOutputLanguageAsync(string connectionId, string lang)
        {
            var safeLang = NormalizeLanguage(lang, DefaultOutputLang);
            if (_preferences.TryGetValue(connectionId, out var pref))
                pref.OutputLang = safeLang;
            return Task.CompletedTask;
        }

        private async Task RecomputeMajorityInputAsync(string meetingId)
        {
            if (string.IsNullOrWhiteSpace(meetingId)) return;

            var members = _preferences
                .Where(kv => kv.Value.MeetingId == meetingId)
                .Select(kv => kv.Value)
                .ToList();

            if (members.Count == 0) return;

            var grouped = members
                .GroupBy(p => NormalizeLanguage(p.InputLang, DefaultInputLang))
                .Select(g => new
                {
                    Lang = g.Key,
                    Count = g.Count(),
                    LatestChange = g.Max(p => p.LastInputChangedUtc)
                })
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.LatestChange)
                .ToList();

            var top = grouped.First();
            if (grouped.Count > 1)
            {
                var second = grouped[1];
                var hasTie = top.Count == second.Count;
                if (hasTie && top.LatestChange == second.LatestChange)
                    return; // tie with no recent change -> keep current
            }

            var currentInput = NormalizeLanguage(_engine.InputLang, DefaultInputLang);
            if (string.Equals(currentInput, top.Lang, StringComparison.OrdinalIgnoreCase)) return;

            if (_engine.IsRunning &&
                string.Equals(_engine.CurrentMeetingId, meetingId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "[COORD] Majority input changed for {MeetingId}: {From} -> {To}",
                    meetingId, currentInput, top.Lang);
                await _engine.SwitchInputAsync(top.Lang);
                return;
            }

            _engine.SetInput(top.Lang);
        }

        private void HandleSourceCommitted(string sourceText)
        {
            if (string.IsNullOrWhiteSpace(sourceText)) return;
            var committedMeetingId = _engine.CurrentMeetingId;
            if (!string.IsNullOrWhiteSpace(committedMeetingId)) BumpEpoch(committedMeetingId);
            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchCommittedAsync(sourceText);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[COORD] Failed to dispatch committed source text");
                }
            });
        }

        private void HandleActiveSource(IReadOnlyDictionary<string, string>? sdkTranslations, string sourceText)
        {
            if (string.IsNullOrWhiteSpace(sourceText)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchActiveAsync(sdkTranslations, sourceText);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[COORD] Failed to dispatch active source text");
                }
            });
        }

        private async Task DispatchActiveAsync(
            IReadOnlyDictionary<string, string>? sdkTranslations,
            string sourceText)
        {
            var meetingId = _engine.CurrentMeetingId;
            if (string.IsNullOrWhiteSpace(meetingId)) return;

            var groups = _preferences
                .Where(kv => kv.Value.MeetingId == meetingId)
                .GroupBy(
                    kv => NormalizeLanguage(kv.Value.OutputLang, DefaultOutputLang),
                    kv => kv.Key)
                .Select(g => new
                {
                    TargetLang = g.Key,
                    ConnectionIds = g.Distinct(StringComparer.Ordinal).ToList()
                })
                .ToList();

            if (groups.Count == 0) return;

            var tasks = groups.Select(async group =>
            {
                string activeText;

                if (sdkTranslations != null)
                {
                    if (!sdkTranslations.TryGetValue(group.TargetLang, out var sdkActiveText) ||
                        string.IsNullOrWhiteSpace(sdkActiveText))
                    {
                        activeText = GetTranslatingIndicator(group.TargetLang);
                    }
                    else
                    {
                        activeText = sdkActiveText;
                    }
                }
                else
                {
                    var src = CurrentSourceLang();
                    if (!IsSameLanguage(src, group.TargetLang))
                    {
                        // Kirim ke antrean "latest wins": tidak ada panggilan menumpuk saat API lebih
                        // lambat dari tick partial (300 ms). Hasil dikirim oleh worker itu sendiri.
                        await TranslateActiveCoalescedAsync(meetingId, group.TargetLang, sourceText);
                        return;
                    }
                    activeText = sourceText;
                }

                var activeConnections = group.ConnectionIds
                    .Where(id =>
                        _preferences.TryGetValue(id, out var p) &&
                        string.Equals(p.MeetingId, meetingId, StringComparison.Ordinal))
                    .ToList();

                if (activeConnections.Count == 0) return;

                await _hub.Clients.Clients(activeConnections)
                    .SendAsync("ReceiveActive", activeText);
            });

            await Task.WhenAll(tasks);
        }

        private void HandleCommittedTranslations(
            IReadOnlyDictionary<string, string> sdkTranslations, string sourceText)
        {
            _ = Task.Run(async () =>
            {
                await _dispatchLock.WaitAsync();
                try
                {
                    await DispatchCommittedFromSdkAsync(sdkTranslations, sourceText);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[COORD] Failed to dispatch direct committed translations");
                }
                finally
                {
                    _dispatchLock.Release();
                }
            });
        }

        private async Task DispatchCommittedAsync(string sourceText)
        {
            if (_engine.Mode == TranslationMode.Direct) return;

            await _dispatchLock.WaitAsync();
            try
            {
                var meetingId = _engine.CurrentMeetingId;
                if (string.IsNullOrWhiteSpace(meetingId)) return;

                var recipients = _preferences
                    .Where(kv => kv.Value.MeetingId == meetingId)
                    .GroupBy(
                        kv => NormalizeLanguage(kv.Value.OutputLang, DefaultOutputLang),
                        kv => kv.Key)
                    .Select(g => new
                    {
                        TargetLang = g.Key,
                        ConnectionIds = g
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Distinct(StringComparer.Ordinal)
                            .ToList()
                    })
                    .Where(g => g.ConnectionIds.Count > 0)
                    .ToList();

                if (recipients.Count == 0) return;

                var fallbackLang = NormalizeLanguage(_engine.InputLang, DefaultInputLang);
                var sourceLang = NormalizeLanguage(_engine.DetectedLang, fallbackLang);
                var dispatchTasks = recipients
                    .Select(group => DispatchToLanguageGroupAsync(
                        sourceText,
                        sourceLang,
                        group.TargetLang,
                        meetingId,
                        group.ConnectionIds))
                    .ToList();

                await Task.WhenAll(dispatchTasks);
            }
            finally
            {
                _dispatchLock.Release();
            }
        }

        private async Task DispatchToLanguageGroupAsync(
            string sourceText,
            string sourceLang,
            string targetLang,
            string meetingId,
            List<string> connectionIds)
        {
            if (connectionIds.Count == 0) return;

            var translated = sourceText;
            if (!IsSameLanguage(sourceLang, targetLang))
            {
                try
                {
                    translated = await _ai.TranslateAsync(
                        sourceText,
                        sourceLang,
                        targetLang,
                        $"{meetingId}|committed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[COORD] Translation failed for {Source}->{Target}", sourceLang, targetLang);
                    translated = sourceText;
                }
            }

            if (!TryPrepareCommitted(
                    meetingId,
                    targetLang,
                    sourceText,
                    translated,
                    out var finalTranslation))
            {
                await _hub.Clients.Clients(connectionIds)
                    .SendAsync("ReceiveActive", EmptyActiveText);
                return;
            }

            var activeRecipients = connectionIds
                .Where(id =>
                    _preferences.TryGetValue(id, out var pref) &&
                    string.Equals(pref.MeetingId, meetingId, StringComparison.Ordinal))
                .ToList();

            if (activeRecipients.Count == 0) return;

            await _hub.Clients.Clients(activeRecipients)
                .SendAsync("ReceiveCommitted", finalTranslation);
            await _hub.Clients.Clients(activeRecipients).SendAsync("ReceiveActive", EmptyActiveText);
        }

        private async Task DispatchCommittedFromSdkAsync(
            IReadOnlyDictionary<string, string> sdkTranslations,
            string sourceText)
        {
            var meetingId = _engine.CurrentMeetingId;
            if (string.IsNullOrWhiteSpace(meetingId)) return;

            var groups = _preferences
                .Where(kv => kv.Value.MeetingId == meetingId)
                .GroupBy(
                    kv => NormalizeLanguage(kv.Value.OutputLang, DefaultOutputLang),
                    kv => kv.Key)
                .Select(g => new
                {
                    TargetLang = g.Key,
                    ConnectionIds = g.Distinct(StringComparer.Ordinal).ToList()
                })
                .ToList();

            if (groups.Count == 0) return;

            var tasks = groups.Select(async group =>
            {
                sdkTranslations.TryGetValue(group.TargetLang, out var translated);

                var activeConnections = group.ConnectionIds
                    .Where(id =>
                        _preferences.TryGetValue(id, out var p) &&
                        string.Equals(p.MeetingId, meetingId, StringComparison.Ordinal))
                    .ToList();
                if (activeConnections.Count == 0) return;

                if (!TryPrepareCommitted(
                        meetingId,
                        group.TargetLang,
                        sourceText,
                        translated ?? "",
                        out var finalTranslation))
                {
                    await _hub.Clients.Clients(activeConnections)
                        .SendAsync("ReceiveActive", EmptyActiveText);
                    return;
                }

                await _hub.Clients.Clients(activeConnections)
                    .SendAsync("ReceiveCommitted", finalTranslation);

                await _hub.Clients.Clients(activeConnections)
                    .SendAsync("ReceiveActive", EmptyActiveText);
            });

            await Task.WhenAll(tasks);
        }

        private static string GetTranslatingIndicator(string lang) => lang switch
        {
            "id" => "Menerjemahkan...",
            "ja" => "翻訳中...",
            "ko" => "번역 중...",
            "ms" => "Menterjemah...",
            "th" => "กำลังแปล...",
            "vi" => "Đang dịch...",
            "fil" => "Nagsasalin...",
            "km" => "កំពុងបកប្រែ...",
            "my" => "ဘာသာပြန်နေသည်...",
            "lo" => "ກໍາລັງແປ...",
            "ta" => "மொழிபெயர்க்கிறது...",
            _ => "Translating..."
        };

        private static string NormalizeLanguage(string? lang, string fallback)
        {
            if (string.IsNullOrWhiteSpace(lang)) return fallback;
            var normalized = lang.Trim().ToLowerInvariant();
            normalized = normalized switch
            {
                "jp" => "ja",
                _ => normalized
            };
            var dash = normalized.IndexOf('-');
            return dash > 0 ? normalized[..dash] : normalized;
        }

        private static bool IsSameLanguage(string source, string target)
            => string.Equals(
                NormalizeLanguage(source, DefaultInputLang),
                NormalizeLanguage(target, DefaultOutputLang),
                StringComparison.OrdinalIgnoreCase);

        private bool TryPrepareCommitted(
            string meetingId,
            string targetLang,
            string sourceText,
            string candidate,
            out string finalTranslation)
        {
            var key = $"{meetingId}|{targetLang}";
            var state = _committedHistory.GetOrAdd(key, _ => new TranslationDispatchState());
            var normalizedSource = NormalizeForCompare(sourceText);
            var normalizedCandidate = NormalizeForCompare(candidate);
            var now = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(normalizedCandidate) ||
                IsTooShortTranslation(normalizedCandidate, normalizedSource))
            {
                if (!string.IsNullOrWhiteSpace(state.LastCommitted) &&
                    (now - state.LastCommittedAtUtc).TotalSeconds <= 30)
                {
                    normalizedCandidate = state.LastCommitted;
                }
                else
                {
                    normalizedCandidate = normalizedSource;
                }
            }

            if (ShouldSkipSimilar(normalizedCandidate, state.LastCommitted, state.LastCommittedAtUtc, now))
            {
                finalTranslation = normalizedCandidate;
                return false;
            }

            state.LastCommitted = normalizedCandidate;
            state.LastCommittedAtUtc = now;
            finalTranslation = normalizedCandidate;
            return true;
        }

        private static string NormalizeForCompare(string text)
            => string.Join(" ",
                (text ?? "").Trim().Split(new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries));

        private static bool ShouldSkipSimilar(
            string current,
            string previous,
            DateTime previousAtUtc,
            DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(previous)) return false;
            var elapsed = (nowUtc - previousAtUtc).TotalSeconds;
            if (elapsed > 6) return false;
            if (string.Equals(current, previous, StringComparison.OrdinalIgnoreCase)) return true;

            if (current.Length > previous.Length + 4 &&
                current.Contains(previous, StringComparison.OrdinalIgnoreCase))
                return false;

            var similarity = ComputeSimilarity(current, previous);
            if (similarity >= 0.9)
            {
                if (current.Length > previous.Length + 4) return false;
                return true;
            }
            return false;
        }

        private static double ComputeSimilarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0.0;
            var nonLatin = ContainsNonLatinScript(a) || ContainsNonLatinScript(b);
            return nonLatin ? TextSimilarity(a, b) : WordJaccard(a, b);
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

        private static double TextSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            if (a == b) return 1.0;
            var longer = a.Length >= b.Length ? a : b;
            var shorter = a.Length >= b.Length ? b : a;
            if (longer.Contains(shorter, StringComparison.OrdinalIgnoreCase))
                return (double)shorter.Length / longer.Length;
            int matches = 0, total = Math.Max(a.Length, b.Length);
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) matches++;
            return (double)matches / total;
        }

        private static double WordJaccard(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0.0;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1.0;
            var wa = new HashSet<string>(
                a.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            var wb = new HashSet<string>(
                b.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            int intersection = 0;
            foreach (var w in wa) if (wb.Contains(w)) intersection++;
            int union = wa.Count + wb.Count - intersection;
            return union == 0 ? 0.0 : (double)intersection / union;
        }

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

        // ── Partial translation: coalescing + guard epoch ────────────────────────
        private int GetEpoch(string meetingId) => _commitEpochs.TryGetValue(meetingId, out var e) ? e : 0;
        private void BumpEpoch(string meetingId) => _commitEpochs.AddOrUpdate(meetingId, 1, (_, v) => v + 1);

        private string CurrentSourceLang() => NormalizeLanguage(
            _engine.DetectedLang,
            NormalizeLanguage(_engine.InputLang, DefaultInputLang));

        private List<string> GetGroupConnections(string meetingId, string targetLang)
            => _preferences
                .Where(kv => kv.Value.MeetingId == meetingId &&
                             string.Equals(NormalizeLanguage(kv.Value.OutputLang, DefaultOutputLang),
                                           targetLang, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();

        private async Task TranslateActiveCoalescedAsync(string meetingId, string targetLang, string firstText)
        {
            var state = _activeStates.GetOrAdd($"{meetingId}|{targetLang}", _ => new ActiveTranslateState());
            var epoch = GetEpoch(meetingId);

            lock (state.Gate)
            {
                if (state.Busy)
                {
                    state.PendingText  = firstText;   // timpa: hanya teks TERBARU yang penting
                    state.PendingEpoch = epoch;
                    return;
                }
                state.Busy = true;
            }

            var text = firstText;
            try
            {
                while (true)
                {
                    string activeText;
                    try
                    {
                        var src = CurrentSourceLang();
                        activeText = IsSameLanguage(src, targetLang)
                            ? text
                            : await _ai.TranslateAsync(text, src, targetLang, $"{meetingId}|active");
                        if (string.IsNullOrWhiteSpace(activeText)) activeText = text;
                    }
                    catch
                    {
                        activeText = text;
                    }

                    // Buang hasil jika sudah ada commit setelah teks ini diminta.
                    if (epoch == GetEpoch(meetingId))
                    {
                        var conns = GetGroupConnections(meetingId, targetLang);
                        if (conns.Count > 0)
                            await _hub.Clients.Clients(conns).SendAsync("ReceiveActive", activeText);
                    }

                    // Ambil teks pending TERBARU bila masih berlaku (belum ada commit sesudahnya).
                    lock (state.Gate)
                    {
                        if (state.PendingText is not null && state.PendingEpoch == GetEpoch(meetingId))
                        {
                            text  = state.PendingText;
                            epoch = state.PendingEpoch;
                            state.PendingText = null;
                        }
                        else
                        {
                            state.PendingText = null;
                            state.Busy = false;
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[COORD] Active translation worker failed");
                lock (state.Gate) { state.Busy = false; state.PendingText = null; }
            }
        }

        public void Dispose()
        {
            _engine.OnFinalOriginal -= HandleSourceCommitted;
            _engine.OnActiveSource -= HandleActiveSource;
            _engine.OnCommittedTranslations -= HandleCommittedTranslations;
            _dispatchLock.Dispose();
        }

        private sealed class ConnectionPreference
        {
            public string MeetingId { get; set; } = "default";
            public string InputLang { get; set; } = DefaultInputLang;
            public string OutputLang { get; set; } = DefaultOutputLang;
            public DateTime LastInputChangedUtc { get; set; } = DateTime.UtcNow;
        }

        private sealed class ActiveTranslateState
        {
            public readonly object Gate = new();
            public bool Busy;
            public string? PendingText;
            public int PendingEpoch;
        }

        private sealed class TranslationDispatchState
        {
            public string LastCommitted { get; set; } = "";
            public DateTime LastCommittedAtUtc { get; set; } = DateTime.MinValue;
        }
    }
}