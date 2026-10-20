using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Teams_Translation_App.Hubs;
using TranslationDefaults = Teams_Translation_App.Services.TranslationDefaults;

namespace LiveTranslation.Engine
{
    public class LiveTranslationEngine : IDisposable
    {
        // ── Azure Speech objects ─────────────────────────────────────────
        private SpeechTranslationConfig?  transConfig;
        private TranslationRecognizer?    transRecognizer;
        private AudioConfig?              audioConfigA;
        private PushAudioInputStream?     pushStreamA;          // ← bot push audio ke sini

        private SpeechConfig?             sttConfig;
        private SpeechRecognizer?         sttRecognizer;
        private AudioConfig?              audioConfigB;
        private PushAudioInputStream?     pushStreamB;          // ← bot push audio ke sini

        // ── Dependency baru: SignalR + Config ────────────────────────────
        private readonly IHubContext<TranslationHub> _hub;
        private readonly IConfiguration              _config;
        private string _meetingId = "default";                  // di-set saat StartAsync()

        // ── AI Translation (tidak berubah) ───────────────────────────────
        private LiveTranslation.AI.AITranslationEngine? openAi;

        // ── State (tidak berubah dari WinForms) ─────────────────────────
        private string          inputLang        = TranslationDefaults.DefaultInputLanguage;
        private string          outputLang       = TranslationDefaults.DefaultOutputLanguage;
        private TranslationMode translationMode  = TranslationMode.SttOpenAI;
        private List<string>    phrases          = new();

        private string   detectedLang        = "id";
        private readonly object dedupA        = new();
        private readonly object dedupB        = new();
        private string   lastFinalTransOrig   = "";
        private DateTime lastFinalTransTime   = DateTime.MinValue;
        private string   lastHistoryOrigA     = "";
        private string   lastHistoryOrigB     = "";
        private DateTime lastHistoryOrigBAt   = DateTime.MinValue;
        private string   lastHistoryTransA    = "";
        private DateTime lastHistoryTransAAt  = DateTime.MinValue;
        private string   lastHistoryTransB    = "";
        private DateTime lastHistoryTransBAt  = DateTime.MinValue;
        private string   lastFinalSttText     = "";
        private DateTime lastFinalSttTime     = DateTime.MinValue;
        private DateTime lastActivityTime     = DateTime.Now;
        private System.Threading.Timer? silenceTimer;
        private const int SilenceClearMs      = 5000;

        private string   partialA_last        = "";
        private DateTime partialA_stableAt    = DateTime.MinValue;
        private string   partialA_committed   = "";
        private string   partialA_committedOrig = "";
        private readonly object lockA         = new();
        private System.Threading.Timer? chunkTimerA;

        private string   partialB_last        = "";
        private DateTime partialB_stableAt    = DateTime.MinValue;
        private string   partialB_committed   = "";
        private string   lastChunkedText      = "";
        private string   utteranceCommittedText = ""; // gabungan SEMUA chunk yang sudah ter-commit di utterance ini (mode B)
        private string   lastCommittedSourceText = "";
        private readonly object lockB         = new();
        private System.Threading.Timer? chunkTimerB;

        private string   activeDebounceText   = "";
        private DateTime activeDebounceAt     = DateTime.MinValue;
        private string   activeLastSent       = "";
        private DateTime lastRecognizingAt    = DateTime.MinValue;
        private IReadOnlyDictionary<string, string>? _lastPartialTranslations;
        private static readonly IReadOnlyDictionary<string, string> EmptyTranslations
            = new Dictionary<string, string>();

        private string   lastGoodDisplayTrans   = "";
        private DateTime lastGoodDisplayTransAt = DateTime.MinValue;
        private string   lastGoodHistoryTrans   = "";
        private DateTime lastGoodHistoryTransAt = DateTime.MinValue;

        private const double StableMs_A       = 1200;
        private const int    MinWords_A       = 4;
        private const double StableMs_B       = 900;
        private const int    MinWords_B       = 4;
        private const double MaxAgeMs_B       = 2200;
        private const int    HardCapWords_B   = 12;
        // false = caption "committed" hanya muncul saat STT final (1 segmen = 1 baris stabil).
        // Partial tetap tampil dinamis di baris aktif. true = perilaku lama (commit potongan lebih awal).
        private const bool   EnableEarlyChunkCommit = true;
        private const double ActiveDebounceMs = 300;

        private static readonly HashSet<string> SemanticBoundaryWords = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "ya", "nah", "jadi", "tapi", "namun", "karena", "sehingga",
            "kemudian", "selanjutnya", "oke", "baik", "gitu", "begitu",
            "makanya", "intinya", "pokoknya", "artinya",
            "ね", "よ", "よね", "です", "ます", "だ", "だね", "じゃ",
            "so", "but", "however", "therefore", "then", "now",
            "basically", "anyway", "right", "okay", "well",
        };

        // ── Events (tetap ada untuk logging/monitoring opsional) ─────────
        // UI update sekarang via SignalR, tapi events ini bisa dipakai
        // untuk logging, storage history, dll.
        public event Action<string>?                    OnFinalOriginal;
        public event Action<string, string, bool>?      OnFinalQuality;
        public event Action<string>?                    OnLanguageDetected;
        public event Action<string>?                    OnError;
        public event Action?                            OnSilenceDetected;
        public event Action<IReadOnlyDictionary<string, string>?, string>? OnActiveSource;
        public event Action<IReadOnlyDictionary<string, string>, string>? OnCommittedTranslations;

        public bool            IsRunning          { get; private set; }
        public string          TranslationModeStr => translationMode == TranslationMode.SttOpenAI ? "STT+OpenAI" : "Direct";
        public string          DetectedLang       => detectedLang;
        public string          InputLang          => inputLang;
        public string          OutputLang         => outputLang;
        public string          CurrentMeetingId   => _meetingId;
        public TranslationMode Mode               => translationMode;

        // ── Konstruktor: terima IHubContext dan IConfiguration via DI ────
        public LiveTranslationEngine(
            IHubContext<TranslationHub> hub,
            IConfiguration config,
            LiveTranslation.AI.AITranslationEngine ai)
        {
            _hub    = hub;
            _config = config;
            openAi  = ai;
        }

        // ── Public setters (tidak berubah) ───────────────────────────────
        public void SetInput(string lang)                    => inputLang      = lang;
        public void SetOutput(string lang)                   => outputLang     = lang;
        public void SetTranslationMode(TranslationMode mode) => translationMode = mode;
        public void SetPhrases(IEnumerable<string> list)
        {
            phrases = new List<string>(list);
            Console.WriteLine($"[ENGINE] PhraseList: {phrases.Count} phrases");
        }
        public void AddPhrases(IEnumerable<string> list) => phrases.AddRange(list);

        public async Task RestartRecognizerAsync()
        {
            if (!IsRunning) return;
            await StopAsync();
            await StartAsync(_meetingId);
        }

        // ── Expose push streams untuk AudioStreamService ─────────────────
        // Bot teman push audio bytes ke sini via AudioStreamService
        public PushAudioInputStream? PushStreamA => pushStreamA;
        public PushAudioInputStream? PushStreamB => pushStreamB;

        // ── StartAsync: terima meetingId untuk SignalR group targeting ────
        public async Task StartAsync(string meetingId = "default")
        {
            if (IsRunning) return;
            _meetingId = meetingId;

            // Baca config via IConfiguration (bukan ConfigurationBuilder manual)
            var key    = _config["Speech:Key"]
                ?? throw new Exception("Speech:Key missing in appsettings.json");
            var region = _config["Speech:Region"]
                ?? throw new Exception("Speech:Region missing in appsettings.json");

            ResetChunkState();
            openAi?.ClearCache();

            // Buat PushAudioInputStream langsung — tidak perlu NAudio capture
            // Audio akan di-push oleh AudioStreamService dari WebSocket bot
            var azureFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
            pushStreamA  = AudioInputStream.CreatePushStream(azureFormat);
            audioConfigA = AudioConfig.FromStreamInput(pushStreamA);
            pushStreamB  = AudioInputStream.CreatePushStream(azureFormat);
            audioConfigB = AudioConfig.FromStreamInput(pushStreamB);

            transConfig = SpeechTranslationConfig.FromSubscription(key, region);
            transConfig.SpeechRecognitionLanguage =
                inputLang == "auto" ? "id-ID" : ToSdkLang(inputLang);
            if (inputLang != "auto") detectedLang = inputLang;
            transConfig.AddTargetLanguage("id");
            transConfig.AddTargetLanguage("en");
            transConfig.AddTargetLanguage("ja");
            transConfig.AddTargetLanguage("ko");
            transConfig.AddTargetLanguage("ms");
            transConfig.AddTargetLanguage("th");
            transConfig.AddTargetLanguage("vi");
            transConfig.AddTargetLanguage("fil");
            transConfig.AddTargetLanguage("km");
            transConfig.AddTargetLanguage("my");
            transConfig.AddTargetLanguage("lo");
            transConfig.AddTargetLanguage("ta");
            transConfig.SetProperty(
                PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "380");
            transConfig.SetProperty(
                PropertyId.Speech_SegmentationSilenceTimeoutMs, "320");
            transConfig.SetProperty(
                PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "3");
            transConfig.SetProperty(
                PropertyId.SpeechServiceResponse_PostProcessingOption, "TrueText");
            transConfig.SetProfanity(ProfanityOption.Masked);

            var autoDetectLanguages = BuildAutoDetectLanguages(inputLang);

            transRecognizer = inputLang == "auto"
                ? new TranslationRecognizer(transConfig,
                    AutoDetectSourceLanguageConfig.FromLanguages(
                        autoDetectLanguages), audioConfigA)
                : new TranslationRecognizer(transConfig, audioConfigA);

            if (phrases.Count > 0)
            {
                var plA = PhraseListGrammar.FromRecognizer(transRecognizer);
                foreach (var p in phrases) plA.AddPhrase(p);
            }

            sttConfig = SpeechConfig.FromSubscription(key, region);
            sttConfig.SetProfanity(ProfanityOption.Masked);
            sttConfig.OutputFormat = OutputFormat.Detailed;
            sttConfig.RequestWordLevelTimestamps();
            sttConfig.SetProperty(
                PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "450");
            sttConfig.SetProperty(
                PropertyId.Speech_SegmentationSilenceTimeoutMs, "380");
            sttConfig.SetProperty(
                PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "3");
            sttConfig.SetProperty(
                PropertyId.SpeechServiceResponse_PostProcessingOption, "TrueText");
            try { sttConfig.SetProperty(
                PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous"); }
            catch { }

            if (inputLang == "auto")
            {
                sttRecognizer = new SpeechRecognizer(sttConfig,
                    AutoDetectSourceLanguageConfig.FromLanguages(
                        autoDetectLanguages), audioConfigB);
            }
            else
            {
                sttConfig.SpeechRecognitionLanguage = ToSdkLang(inputLang);
                sttRecognizer = new SpeechRecognizer(sttConfig, audioConfigB);
            }

            if (phrases.Count > 0)
            {
                var plB = PhraseListGrammar.FromRecognizer(sttRecognizer);
                foreach (var p in phrases) plB.AddPhrase(p);
            }

            WireTranslationEvents();
            WireSttEvents();

            // Tidak ada captureA/B.StartRecording() — audio dari bot
            await transRecognizer.StartContinuousRecognitionAsync();
            await sttRecognizer.StartContinuousRecognitionAsync();

            IsRunning        = true;
            lastActivityTime = DateTime.Now;

            silenceTimer = new System.Threading.Timer(_ =>
            {
                if ((DateTime.Now - lastActivityTime).TotalMilliseconds > SilenceClearMs)
                    OnSilenceDetected?.Invoke();
            }, null, 1000, 1000);

            chunkTimerA = new System.Threading.Timer(
                _ => TryForceCommitA(), null, 300, 300);
            chunkTimerB = new System.Threading.Timer(
                _ => TryForceCommitB(), null, 300, 300);

            await _hub.Clients.Group(_meetingId)
                .SendAsync("EngineStarted", _meetingId);

            Console.WriteLine($"[ENGINE] START: mode={translationMode} input={inputLang} output={outputLang} meeting={_meetingId}");
        }

        public async Task StopAsync()
        {
            if (!IsRunning) return;
            IsRunning = false;

            silenceTimer?.Dispose(); silenceTimer = null;
            chunkTimerA?.Dispose();  chunkTimerA  = null;
            chunkTimerB?.Dispose();  chunkTimerB  = null;

            // Tidak ada capture.StopRecording() — tidak ada NAudio

            if (transRecognizer != null)
            {
                try { await transRecognizer.StopContinuousRecognitionAsync(); } catch { }
                transRecognizer.Dispose(); transRecognizer = null;
            }
            if (sttRecognizer != null)
            {
                try { await sttRecognizer.StopContinuousRecognitionAsync(); } catch { }
                sttRecognizer.Dispose(); sttRecognizer = null;
            }

            try { audioConfigA?.Dispose(); audioConfigA = null; } catch { }
            try { audioConfigB?.Dispose(); audioConfigB = null; } catch { }
            try { pushStreamA?.Close();    pushStreamA  = null; } catch { }
            try { pushStreamB?.Close();    pushStreamB  = null; } catch { }

            await _hub.Clients.Group(_meetingId)
                .SendAsync("EngineStopped", _meetingId);

            Console.WriteLine("[ENGINE] Stopped");
        }

        // ── Helper: push ke SignalR (menggantikan OnCaptionActive?.Invoke) ─
        // Fire-and-forget karena dipanggil dari event handler sync
        private void PushActive(string text)
        {
            if (!IsRunning || string.IsNullOrWhiteSpace(_meetingId)) return;
            _ = _hub.Clients.Group(_meetingId).SendAsync("ReceiveActive", text ?? "");
        }

        private void PushCommitted(string text)
        {
            // Committed translation dispatch is handled externally per user output language.
        }

        private static string GetTranslatingText(string lang) => lang switch
        {
            "id"  => "Menerjemahkan...",
            "ja"  => "翻訳中...",
            "ko"  => "번역 중...",
            "ms"  => "Menterjemah...",
            "th"  => "กำลังแปล...",
            "vi"  => "Đang dịch...",
            "fil" => "Nagsasalin...",
            "km"  => "កំពុងបកប្រែ...",
            "my"  => "ဘာသာပြန်နေသည်...",
            "lo"  => "ກໍາລັງແປ...",
            "ta"  => "மொழிபெயர்க்கிறது...",
            "en"  => "Translating...",
            _     => "Translating..."
        };

        // ── WireTranslationEvents (sama persis, hanya ganti Invoke → Push) ─
        private void WireTranslationEvents()
        {
            transRecognizer!.Recognizing += (_, e) =>
            {
                if (e.Result.Reason != ResultReason.TranslatingSpeech) return;
                lastActivityTime = DateTime.Now;
                TryDetectLangFromTrans(e.Result);
                _lastPartialTranslations = e.Result.Translations;

                var liveOriginal = e.Result.Text ?? "";
                var live = GetTranslation(e.Result.Translations, outputLang, liveOriginal);

                if (translationMode == TranslationMode.Direct)
                {
                    OnActiveSource?.Invoke(e.Result.Translations, liveOriginal);
                }

                // Simpan partial meski translation interim kosong (mis. th/km/my)
                var textToStore = !string.IsNullOrWhiteSpace(live) ? live : liveOriginal;
                if (string.IsNullOrWhiteSpace(textToStore)) return;

                lock (lockA)
                {
                    if (textToStore != partialA_last)
                    {
                        partialA_last     = textToStore;
                        partialA_stableAt = DateTime.Now;
                    }
                }
            };

            transRecognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason != ResultReason.TranslatedSpeech) return;
                if (string.IsNullOrWhiteSpace(e.Result.Text)) return;

                var original      = e.Result.Text;
                var sdkTranslated = GetTranslation(e.Result.Translations, outputLang, original);

                // Jika SDK belum memberi translation final, jangan update dedup/clear state.
                // Biarkan partial tetap hidup agar force-commit/recognized berikutnya masih bisa memproses.
                if (translationMode == TranslationMode.Direct &&
                    string.IsNullOrWhiteSpace(sdkTranslated))
                    return;

                lock (dedupA)
                {
                    if (original == lastFinalTransOrig &&
                        (DateTime.Now - lastFinalTransTime).TotalSeconds < 5) return;

                    if (!string.IsNullOrEmpty(lastFinalTransOrig))
                    {
                        var elapsedA = (DateTime.Now - lastFinalTransTime).TotalSeconds;
                        var simA     = TextSimilarity(original, lastFinalTransOrig);
                        if (simA > 0.92 && elapsedA < 4.0) return;
                    }

                    lastFinalTransOrig = original;
                    lastFinalTransTime = DateTime.Now;
                }

                TryDetectLangFromTrans(e.Result);
                lastActivityTime = DateTime.Now;

                string chunkedA, chunkedOrigA, lastActiveA;
                lock (lockA)
                {
                    chunkedA     = partialA_committed;
                    chunkedOrigA = partialA_committedOrig;
                    lastActiveA  = partialA_last;
                    partialA_last          = "";
                    partialA_committed     = "";
                    partialA_committedOrig = "";
                }

                OnFinalOriginal?.Invoke(original);

                if (translationMode == TranslationMode.Direct)
                {
                    string sdkSafe       = sdkTranslated ?? "";
                    string lastActiveSafe = lastActiveA ?? "";
                    if (string.IsNullOrWhiteSpace(sdkSafe))
                    {
                        // Azure belum memberi translation final untuk bahasa tertentu.
                        return;
                    }

                    // Ada translation dari Azure — proses normal
                    var toEmitA = string.IsNullOrWhiteSpace(chunkedA)
                        ? sdkSafe
                        : StripChunkedPrefix(sdkSafe, chunkedA);
                    if (string.IsNullOrWhiteSpace(toEmitA)) toEmitA = sdkSafe;
                    if (string.IsNullOrWhiteSpace(toEmitA)) toEmitA = lastActiveSafe;

                    if (!string.IsNullOrWhiteSpace(toEmitA))
                        OnCommittedTranslations?.Invoke(e.Result.Translations, original);

                    PushActive("");              // ← ganti OnCaptionActive?.Invoke("")

                    // History logic (tidak berubah)
                    bool shouldSaveHistory = true;
                    if (!string.IsNullOrWhiteSpace(lastHistoryOrigA))
                    {
                        var prev        = lastHistoryOrigA.Trim().TrimEnd('.','?','!','。','？','！',',','،');
                        var curr        = original.Trim();
                        var prevWords   = new HashSet<string>(
                            prev.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
                            StringComparer.OrdinalIgnoreCase);
                        var currWords   = new HashSet<string>(
                            curr.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
                            StringComparer.OrdinalIgnoreCase);
                        int prevInCurr  = 0;
                        foreach (var w in prevWords) if (currWords.Contains(w)) prevInCurr++;
                        double subsetRatio = prevWords.Count > 0 ? (double)prevInCurr / prevWords.Count : 0;
                        bool isSuperset    = subsetRatio >= 0.80 && curr.Length > prev.Length + 5;

                        if (isSuperset)
                        { shouldSaveHistory = false; lastHistoryOrigA = curr; }
                        else if (WordJaccard(curr, prev) > 0.75 &&
                                 (DateTime.Now - lastFinalTransTime).TotalSeconds < 8)
                        { shouldSaveHistory = false; }
                        else
                        { lastHistoryOrigA = curr; }
                    }
                    else lastHistoryOrigA = original.Trim();

                    if (shouldSaveHistory)
                    {
                        if (!ShouldSkipSimilarTranslation(
                                sdkSafe,
                                ref lastHistoryTransA,
                                ref lastHistoryTransAAt))
                        {
                            OnFinalQuality?.Invoke(original, sdkSafe, false);
                        }
                    }
                }
            };

            transRecognizer.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                {
                    Console.WriteLine($"[A ERROR] {e.ErrorDetails}");
                    OnError?.Invoke(e.ErrorDetails);
                }
            };

            transRecognizer.SessionStarted += (_, _) => Console.WriteLine("[A] Session started");
            transRecognizer.SessionStopped += (_, _) => Console.WriteLine("[A] Session stopped");
        }

        // ── WireSttEvents (ported from Desktop Version) ─────────────────────
        private void WireSttEvents()
        {
            sttRecognizer!.Recognizing += (_, e) =>
            {
                if (e.Result.Reason != ResultReason.RecognizingSpeech) return;
                if (string.IsNullOrWhiteSpace(e.Result.Text)) return;
                lastActivityTime = DateTime.Now;

                lock (lockB)
                {
                    lastRecognizingAt = DateTime.Now;
                    if (e.Result.Text != partialB_last)
                    {
                        partialB_last     = e.Result.Text;
                        partialB_stableAt = DateTime.Now;
                    }
                    if (e.Result.Text != activeDebounceText)
                    {
                        activeDebounceText = e.Result.Text;
                        activeDebounceAt   = DateTime.Now;
                    }
                }

                if (inputLang == "auto")
                {
                    try
                    {
                        var ar = AutoDetectSourceLanguageResult.FromResult(e.Result);
                        if (!string.IsNullOrEmpty(ar?.Language))
                        {
                            var norm = NormLang(ar.Language);
                            if (norm != detectedLang)
                            {
                                detectedLang = norm;
                                OnLanguageDetected?.Invoke(norm);
                            }
                        }
                    }
                    catch { }
                }
            };

            sttRecognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason != ResultReason.RecognizedSpeech) return;
                if (string.IsNullOrWhiteSpace(e.Result.Text)) return;

                var conf = GetConfidence(e.Result);
                if (conf < 0.40)
                {
                    Console.WriteLine($"[B] Low conf {conf:P0}, skip");
                    return;
                }

                lock (dedupB)
                {
                    if (e.Result.Text == lastFinalSttText &&
                        (DateTime.Now - lastFinalSttTime).TotalSeconds < 5) return;

                    if (!string.IsNullOrEmpty(lastFinalSttText))
                    {
                        var elapsed = (DateTime.Now - lastFinalSttTime).TotalSeconds;
                        var sim     = TextSimilarity(e.Result.Text, lastFinalSttText);
                        if (sim > 0.95 && elapsed < 3.0)
                        {
                            Console.WriteLine($"[B] Dedup sim={sim:P0} ({elapsed:F1}s), skip");
                            return;
                        }
                    }
                    lastFinalSttText = e.Result.Text;
                    lastFinalSttTime = DateTime.Now;
                }

                string originalFull = e.Result.Text;
                string chunked;
                string committedAll;
                lock (lockB)
                {
                    chunked                 = lastChunkedText;
                    committedAll            = utteranceCommittedText;
                    utteranceCommittedText  = "";
                    partialB_last           = "";
                    partialB_committed      = "";
                    lastChunkedText         = "";
                    lastCommittedSourceText = "";
                }

                Console.WriteLine($"[B] conf={conf:P0} full='{originalFull}' chunked='{chunked}'");

                string remainder = StripCommittedPrefix(originalFull, committedAll, chunked);

                // OPTIMASI: kirim hanya sisa (remainder) yang belum pernah di-commit sebagai chunk.
                // Versi lama mengirim originalFull sehingga bagian yang sudah ter-commit
                // diterjemahkan ulang (biaya ganda) dan bisa tampil dobel di caption.
                if (translationMode == TranslationMode.SttOpenAI)
                {
                    if (!string.IsNullOrWhiteSpace(remainder))
                        OnFinalOriginal?.Invoke(remainder);
                    else
                        PushActive("");
                }

                FireOpenAiTranslate(
                    historyText: originalFull,
                    displayText: remainder,
                    isChunked:   false);
            };

            sttRecognizer.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                {
                    Console.WriteLine($"[B ERROR] {e.ErrorDetails}");
                    OnError?.Invoke(e.ErrorDetails);
                }
            };

            sttRecognizer.SessionStarted += (_, _) => Console.WriteLine("[B] Session started");
            sttRecognizer.SessionStopped += (_, _) => Console.WriteLine("[B] Session stopped");
        }

        // ── TryForceCommitA (tidak berubah, hanya ganti Invoke → Push) ───
        private void TryForceCommitA()
        {
            if (!IsRunning || translationMode != TranslationMode.Direct) return;

            string text; DateTime stableAt;
            lock (lockA)
            {
                text     = partialA_last;
                stableAt = partialA_stableAt;
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text == partialA_committed) return;
                if (WordCount(text) < MinWords_A) return;
            }

            var stableMs  = (DateTime.Now - stableAt).TotalMilliseconds;
            var sentences = ExtractCompleteSentences(text, out string remainder);

            if (sentences.Count == 0)
            {
                if (stableMs < StableMs_A) return;
                sentences.Add(text);
                remainder = "";
            }

            var toCommit = string.Join(" ", sentences).Trim();
            if (string.IsNullOrWhiteSpace(toCommit)) return;
            if (toCommit == partialA_committed) return;

            lock (lockA)
            {
                partialA_committed = toCommit;
                partialA_last      = remainder;
                partialA_stableAt  = DateTime.Now;
            }

            OnCommittedTranslations?.Invoke(
                _lastPartialTranslations ?? EmptyTranslations,
                toCommit);
            if (!string.IsNullOrWhiteSpace(remainder))
                OnActiveSource?.Invoke(_lastPartialTranslations, remainder);
            else
                PushActive("");
        }

        // ── TryForceCommitB (ported from Desktop Version) ───────────────────
        private void TryForceCommitB()
        {
            if (!IsRunning || translationMode != TranslationMode.SttOpenAI) return;

            string text;
            bool   shouldTranslateActive = false;
            bool   shouldCommitChunk     = false;
            string commitReason          = "";

            const double SttQuietMs = 420;

            lock (lockB)
            {
                text = partialB_last;
                if (string.IsNullOrWhiteSpace(text)) return;

                var now      = DateTime.Now;
                var stableMs = (now - partialB_stableAt).TotalMilliseconds;
                var debMs    = (now - activeDebounceAt).TotalMilliseconds;
                var quietMs  = (now - lastRecognizingAt).TotalMilliseconds;
                var wc       = WordCount(text);
                bool canCommit = text != partialB_committed;

                bool sttIsQuiet = quietMs >= SttQuietMs;

                if (canCommit && sttIsQuiet)
                {
                    if (wc >= HardCapWords_B)
                    {
                        shouldCommitChunk = true;
                        commitReason      = $"hard-cap({wc}w,quiet={quietMs:F0}ms)";
                    }
                    else if (EnableEarlyChunkCommit && EndsAtSemanticBoundary(text) && wc >= 4 && stableMs >= 220)
                    {
                        shouldCommitChunk = true;
                        commitReason      = $"semantic({wc}w,quiet={quietMs:F0}ms)";
                    }
                    else if (EnableEarlyChunkCommit && stableMs >= StableMs_B && wc >= MinWords_B)
                    {
                        shouldCommitChunk = true;
                        commitReason      = $"stable({stableMs:F0}ms,{wc}w)";
                    }
                    else if (EnableEarlyChunkCommit && quietMs >= 1100 && stableMs >= 650 && wc >= 2)
                    {
                        shouldCommitChunk = true;
                        commitReason      = $"long-pause({stableMs:F0}ms,quiet={quietMs:F0}ms,{wc}w)";
                    }
                    else if (EnableEarlyChunkCommit && stableMs >= MaxAgeMs_B && wc >= MinWords_B)
                    {
                        shouldCommitChunk = true;
                        commitReason      = $"max-age({stableMs:F0}ms,{wc}w)";
                    }

                    if (shouldCommitChunk)
                    {
                        if (!string.IsNullOrEmpty(lastCommittedSourceText))
                        {
                            var prevLen  = lastCommittedSourceText.Length;
                            var checkLen = Math.Min(prevLen, text.Length);
                            if (checkLen > 0 && text.StartsWith(
                                lastCommittedSourceText.Substring(0, Math.Min(prevLen, checkLen)),
                                StringComparison.OrdinalIgnoreCase))
                            {
                                shouldCommitChunk = false;
                                Console.WriteLine($"[B-CHUNK] SUPERSET DEDUP skip: '{text.Substring(0, Math.Min(40, text.Length))}...'");
                            }
                        }
                    }
                }

                if (debMs >= ActiveDebounceMs && text != activeLastSent)
                {
                    shouldTranslateActive = true;
                    activeLastSent        = text;
                }
            }

            if (shouldTranslateActive && !shouldCommitChunk)
            {
                var snap = text;
                _ = Task.Run(() => OnActiveSource?.Invoke(null, snap));
            }

            if (shouldCommitChunk)
            {
                var chunkSnap = PickCommitChunk(text);
                if (!string.IsNullOrWhiteSpace(chunkSnap))
                {
                    lock (lockB)
                    {
                        partialB_committed      = chunkSnap;
                        lastChunkedText         = chunkSnap;
                        lastCommittedSourceText = chunkSnap;
                        utteranceCommittedText  = (utteranceCommittedText + " " + chunkSnap).Trim();

                        if (partialB_last.StartsWith(chunkSnap, StringComparison.OrdinalIgnoreCase))
                        {
                            partialB_last     = partialB_last.Substring(chunkSnap.Length).TrimStart();
                            partialB_stableAt = DateTime.Now;
                        }
                    }

                    Console.WriteLine($"[B-CHUNK] {commitReason}: '{chunkSnap}'");
                    OnFinalOriginal?.Invoke(chunkSnap);
                    FireOpenAiTranslate(chunkSnap, chunkSnap, isChunked: true);
                }
            }
        }

        private static int LastBoundaryIndex(string text)
        {
            return text.LastIndexOfAny(new[] { '.', '?', '!', '。', '？', '！', ',', '、', '，', ';', '；', ':' });
        }

        private static string SanitizeCommitText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text.Trim();
            t = t.TrimEnd('…');
            while (t.EndsWith("...", StringComparison.Ordinal))
                t = t[..^3].TrimEnd();
            return t;
        }

        private static string PickCommitChunk(string text)
        {
            var t = SanitizeCommitText(text);
            if (string.IsNullOrWhiteSpace(t)) return "";

            var b = LastBoundaryIndex(t);
            if (b >= 0)
            {
                var ratio = (double)(b + 1) / Math.Max(1, t.Length);
                if (ratio >= 0.45)
                    return t.Substring(0, b + 1).Trim();
            }

            return t;
        }

        private static string NormalizeDisfluenciesForAi(string text, string lang)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text;
            var normLang = NormalizeLangKey(lang);

            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();

            if (normLang == "th")
            {
                t = System.Text.RegularExpressions.Regex.Replace(
                    t,
                    @"\b(uh+|um+|erm+|ah+|eh+|ehm|hmm|mmm)\b",
                    " ",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                t = System.Text.RegularExpressions.Regex.Replace(
                    t,
                    @"(เอ่อ+|อืม+|เอ้อ+|อ่า+|ฮึม+)",
                    " ",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            else if (normLang == "vi")
            {
                t = System.Text.RegularExpressions.Regex.Replace(
                    t,
                    @"\b(uh+|um+|erm+|ah+|eh+|ehm|hmm|mmm)\b",
                    " ",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                t = System.Text.RegularExpressions.Regex.Replace(
                    t,
                    @"\b(ờ+|ừ+|ừm+|ờm+|à+|ờ\s*thì|ừ\s*thì|ý\s*là|kiểu|kiểu\s*như)\b",
                    " ",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            else
            {
                t = System.Text.RegularExpressions.Regex.Replace(
                    t,
                    @"\b(uh+|um+|erm+|ah+|eh+|e+|you know|like|sort of|kind of|anu|ehm|hmm|mmm)\b",
                    " ",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            t = System.Text.RegularExpressions.Regex.Replace(
                t,
                @"\b(\w+)\s+(\1\b\s*)+",
                "$1 ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim(' ', ',', '.', ';', ':');
            return t;
        }

        private static string NormalizeLangKey(string lang) => lang.ToLowerInvariant() switch
        {
            "jp" or "ja" or "ja-jp" => "ja",
            "id" or "id-id"         => "id",
            "en" or "en-us"         => "en",
            _                       => lang.ToLowerInvariant().Split('-')[0]
        };

        private static bool IsSameLang(string a, string b)
        {
            static string Norm(string s) => s.ToLowerInvariant() switch
            {
                "jp" or "ja" or "ja-jp" => "ja",
                "id" or "id-id"         => "id",
                "en" or "en-us"         => "en",
                _                       => s.ToLowerInvariant().Split('-')[0]
            };
            return Norm(a) == Norm(b);
        }

        // OPTIMASI STT+AI: versi lama memanggil OpenAI di sini dengan dst=outputLang ("ja" default)
        // lalu hasilnya dibuang (PushCommitted kosong, OnFinalQuality tanpa subscriber).
        // Terjemahan sebenarnya dilakukan MeetingTranslationCoordinator per bahasa target user.
        // Method dipertahankan sebagai no-op agar pemanggil tidak perlu diubah; tidak ada biaya OpenAI di sini.
        // Pembersihan caption aktif ("ReceiveActive" kosong) sudah dikirim coordinator setelah committed.
        private void FireOpenAiTranslate(string historyText, string displayText, bool isChunked)
        {
            Console.WriteLine($"[FIRE] no-op (translation handled by coordinator) chunked={isChunked}");
        }

        // ── Semua helper methods berikut TIDAK BERUBAH sama sekali ───────
        private void ResetChunkState()
        {
            lock (lockA)
            {
                partialA_last = ""; partialA_stableAt = DateTime.MinValue;
                partialA_committed = ""; partialA_committedOrig = "";
                lastHistoryOrigA = ""; lastHistoryOrigB = "";
                lastHistoryOrigBAt = DateTime.MinValue;
                lastHistoryTransA = ""; lastHistoryTransAAt = DateTime.MinValue;
                lastHistoryTransB = ""; lastHistoryTransBAt = DateTime.MinValue;
            }
            lock (lockB)
            {
                partialB_last = ""; partialB_stableAt = DateTime.MinValue;
                partialB_committed = ""; lastChunkedText = ""; utteranceCommittedText = "";
                lastCommittedSourceText = ""; lastRecognizingAt = DateTime.MinValue;
                activeDebounceText = ""; activeDebounceAt = DateTime.MinValue;
                activeLastSent = "";
            }
            lastGoodDisplayTrans = ""; lastGoodDisplayTransAt = DateTime.MinValue;
            lastGoodHistoryTrans = ""; lastGoodHistoryTransAt = DateTime.MinValue;
        }

        private static List<string> ExtractCompleteSentences(string text, out string remainder)
        {
            var result = new List<string>();
            remainder  = text.Trim();
            var t      = text.Trim();

            var delimiters = new[] { '.', '?', '!', '。', '？', '！' };
            int lastEnd    = -1;

            for (int i = 0; i < t.Length; i++)
            {
                if (Array.IndexOf(delimiters, t[i]) >= 0)
                {
                    var sentence = t.Substring(lastEnd + 1, i - lastEnd).Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                        result.Add(sentence);
                    lastEnd = i;
                }
            }

            remainder = lastEnd >= 0 ? t.Substring(lastEnd + 1).Trim() : t;
            return result;
        }

        private static bool EndsAtSemanticBoundary(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.TrimEnd();
            if (t.EndsWith('.') || t.EndsWith('?') || t.EndsWith('!') ||
                t.EndsWith('。') || t.EndsWith('？') || t.EndsWith('！') ||
                t.EndsWith(',') || t.EndsWith('、')) return true;
            var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return false;
            var last = words[^1].TrimEnd('.', ',', '?', '!', '。', '、');
            return SemanticBoundaryWords.Contains(last);
        }

        private void TryDetectLangFromTrans(TranslationRecognitionResult result)
        {
            if (inputLang != "auto") return;
            try
            {
                var ar = AutoDetectSourceLanguageResult.FromResult(result);
                if (string.IsNullOrEmpty(ar?.Language)) return;
                var norm = NormLang(ar.Language);
                if (norm == detectedLang) return;
                detectedLang = norm;
                OnLanguageDetected?.Invoke(norm);
            }
            catch { }
        }

        private string GetTranslation(IReadOnlyDictionary<string, string> dict,
            string lang, string fallback)
        {
            var key = lang is "jp" ? "ja" : lang;
            return dict.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
        }

        private static double GetConfidence(SpeechRecognitionResult result)
        {
            try
            {
                var json = result.Properties.GetProperty(
                    PropertyId.SpeechServiceResponse_JsonResult);
                if (!string.IsNullOrEmpty(json))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("NBest", out var nb)
                        && nb.GetArrayLength() > 0)
                        if (nb[0].TryGetProperty("Confidence", out var c))
                            return c.GetDouble();
                }
            }
            catch { }
            return 0.70;
        }

        // Buang SEMUA bagian yang sudah ter-commit sebagai chunk dari hasil final STT.
        // Versi lama (StripChunkedPrefix) hanya mengenali chunk TERAKHIR, jadi kalau satu utterance
        // sudah ter-commit >1 chunk, hasil final dikirim penuh lagi => duplikasi.
        // Strategi: cocokkan N kata pertama final dengan N kata committed (toleran ~20% beda (min. 1 kata
        // untuk n>=4) karena STT bisa merevisi kata), sisanya = remainder. Kalau tidak cocok, pakai logika lama.
        private static string StripCommittedPrefix(string full, string committedAll, string lastChunk)
        {
            if (string.IsNullOrWhiteSpace(committedAll)) return full;

            var fw = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cw = committedAll.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int n  = cw.Length;

            if (fw.Length >= n)
            {
                int same = 0;
                for (int i = 0; i < n; i++)
                    if (string.Equals(NormalizeWordForCompare(fw[i]), NormalizeWordForCompare(cw[i]),
                            StringComparison.OrdinalIgnoreCase)) same++;

                if (same >= n - (n >= 4 ? Math.Max(1, n / 5) : 0))
                {
                    var rem = string.Join(" ", fw.Skip(n)).Trim();
                    return rem.Any(char.IsLetterOrDigit) ? rem : "";
                }
            }
            else if (TextSimilarity(
                         string.Join(" ", fw.Select(NormalizeWordForCompare)).ToLowerInvariant(),
                         string.Join(" ", cw.Select(NormalizeWordForCompare)).ToLowerInvariant()) > 0.8)
            {
                return ""; // final lebih pendek dari yang sudah ter-commit & mirip: tidak ada yang baru
            }

            return StripChunkedPrefix(full, lastChunk);
        }

        private static string StripChunkedPrefix(string fullText, string chunked)
        {
            if (string.IsNullOrWhiteSpace(chunked)) return fullText;
            var f = fullText.Trim();
            var c = chunked.Trim();
            if (f.StartsWith(c, StringComparison.OrdinalIgnoreCase))
            {
                var rem = f.Substring(c.Length).TrimStart();
                return WordCount(rem) >= 3 ? rem : "";
            }
            var fw = f.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cw = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var maxOverlap = Math.Min(fw.Length, cw.Length);
            for (int overlap = maxOverlap; overlap >= 4; overlap--)
            {
                bool same = true;
                for (int i = 0; i < overlap; i++)
                {
                    var a = NormalizeWordForCompare(fw[i]);
                    var b = NormalizeWordForCompare(cw[cw.Length - overlap + i]);
                    if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    { same = false; break; }
                }
                if (!same) continue;
                var rem = string.Join(" ", fw, overlap, fw.Length - overlap).TrimStart();
                return WordCount(rem) >= 3 ? rem : "";
            }
            return TextSimilarity(f, c) > 0.88 ? "" : fullText;
        }

        private static string NormalizeWordForCompare(string word) =>
            word.Trim().Trim(',', '.', ';', ':', '?', '!', '。', '、', '?', '!', '"', '\'');

        private static int WordCount(string text) =>
            string.IsNullOrWhiteSpace(text) ? 0
            : text.Trim().Split(new[] { ' ', '\t', '\n' },
                StringSplitOptions.RemoveEmptyEntries).Length;

        private static double TextSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            if (a == b) return 1.0;
            var longer  = a.Length >= b.Length ? a : b;
            var shorter = a.Length >= b.Length ? b : a;
            if (longer.Contains(shorter)) return (double)shorter.Length / longer.Length;
            int matches = 0, total = Math.Max(a.Length, b.Length);
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] == b[i]) matches++;
            return (double)matches / total;
        }

        private static double WordJaccard(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0.0;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1.0;
            var wa = new HashSet<string>(
                a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            var wb = new HashSet<string>(
                b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            int intersection = 0;
            foreach (var w in wa) if (wb.Contains(w)) intersection++;
            int union = wa.Count + wb.Count - intersection;
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        private static double ComputeTranslationSimilarity(string a, string b)
        {
            var nonLatin = ContainsNonLatinScript(a) || ContainsNonLatinScript(b);
            return nonLatin ? TextSimilarity(a, b) : WordJaccard(a, b);
        }

        private static bool ShouldSkipSimilarTranslation(
            string text,
            ref string lastText,
            ref DateTime lastAt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            var now = DateTime.Now;
            if (string.IsNullOrWhiteSpace(lastText))
            {
                lastText = text;
                lastAt = now;
                return false;
            }

            var elapsed = (now - lastAt).TotalSeconds;
            if (elapsed > 8)
            {
                lastText = text;
                lastAt = now;
                return false;
            }

            if (string.Equals(text.Trim(), lastText.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

            if (text.Length > lastText.Length + 4 &&
                text.Contains(lastText, StringComparison.OrdinalIgnoreCase))
            {
                lastText = text;
                lastAt = now;
                return false;
            }

            var similarity = ComputeTranslationSimilarity(text, lastText);
            if (similarity >= 0.9)
            {
                if (text.Length > lastText.Length + 4)
                {
                    lastText = text;
                    lastAt = now;
                    return false;
                }
                return true;
            }

            lastText = text;
            lastAt = now;
            return false;
        }

        private static string EnsureNonEmptyTranslation(
            string candidate,
            string sourceText,
            ref string lastGood,
            ref DateTime lastGoodAt)
        {
            var trimmed = candidate?.Trim() ?? "";
            var now = DateTime.Now;
            if (string.IsNullOrWhiteSpace(trimmed) || IsTooShortTranslation(trimmed, sourceText))
            {
                if (!string.IsNullOrWhiteSpace(lastGood) &&
                    (now - lastGoodAt).TotalSeconds <= 30)
                    return lastGood;
                return sourceText.Trim();
            }

            lastGood = trimmed;
            lastGoodAt = now;
            return trimmed;
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

        private static string ToSdkLang(string lang) => lang switch
        {
            "id"  => "id-ID",
            "en"  => "en-US",
            "ja"  => "ja-JP",
            "jp"  => "ja-JP",
            "ko"  => "ko-KR",
            "ms"  => "ms-MY",
            "th"  => "th-TH",
            "vi"  => "vi-VN",
            "fil" => "fil-PH",
            "km"  => "km-KH",
            "my"  => "my-MM",
            "lo"  => "lo-LA",
            "ta"  => "ta-MY",
            _     => "en-US"
        };

        private static string NormLang(string sdkLang) => sdkLang switch
        {
            "id-ID"  => "id",
            "en-US"  => "en",
            "ja-JP"  => "ja",
            "ko-KR"  => "ko",
            "ms-MY"  => "ms",
            "th-TH"  => "th",
            "vi-VN"  => "vi",
            "fil-PH" => "fil",
            "km-KH"  => "km",
            "my-MM"  => "my",
            "lo-LA"  => "lo",
            "ta-MY"  => "ta",
            _ => sdkLang.Contains('-') ? sdkLang.Split('-')[0] : sdkLang
        };

        private static string[] BuildAutoDetectLanguages(string selectedInputLang)
        {
            var slot3 = selectedInputLang switch
            {
                "en" or "ja" or "jp" or "auto" => "id-ID",
                _ => ToSdkLang(selectedInputLang)
            };

            if (slot3 is "en-US" or "ja-JP")
                slot3 = "id-ID";

            var slot4 = slot3 == "id-ID" ? "ms-MY" : "id-ID";
            return new[] { "en-US", "ja-JP", slot3, slot4 };
        }

        public async Task SwitchInputAsync(string lang)
        {
            inputLang = lang;
            if (!IsRunning) return;
            await StopAsync();
            await StartAsync(_meetingId);
        }

        public Task SwitchOutputAsync(string lang)
        {
            outputLang = lang;
            return Task.CompletedTask;
        }

        // Mode switch does NOT restart recognizers: both run in parallel already;
        // translationMode only selects which recognizer's output reaches SignalR.
        public async Task SwitchModeAsync(TranslationMode mode)
        {
            translationMode = mode;
            ResetChunkState();
            // Push an empty active text to clear the display on all clients.
            // The await is conditional but the method signature is async Task
            // because Task must be returned to callers using await.
            if (IsRunning)
                await _hub.Clients.Group(_meetingId).SendAsync("ReceiveActive", "");
            Console.WriteLine($"[ENGINE] Mode: {TranslationModeStr}");
        }

        public void Dispose()
        {
            if (IsRunning)
                Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(5));
        }
    }

    public enum TranslationMode
    {
        Direct,
        SttOpenAI
    }
}