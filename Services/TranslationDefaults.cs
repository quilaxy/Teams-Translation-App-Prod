namespace Teams_Translation_App.Services
{
public static class TranslationDefaults
{
    // Sumber tunggal default bahasa: dipakai UI (TranslationTab.razor), koordinator, dan engine.
    public const string DefaultInputLanguage = "id";
    public const string DefaultOutputLanguage = "en";

    // NOTICE [FUTURE-LANG]: Untuk sementara input/output dibatasi ke Indonesia (id), English (en),
    // dan Japanese (ja). Bahasa lain sengaja di-comment, BUKAN dihapus, sebagai calon fitur.
    // Untuk mengaktifkan bahasa lagi, un-comment SEMUA tempat bertanda [FUTURE-LANG]
    // (cari dengan: grep -rn "FUTURE-LANG"):
    //   - daftar ini
    //   - Components/Pages/TranslationTab.razor (_inputOptions & _outputOptions)
    //   - Services/LiveTranslationEngine.cs (AddTargetLanguage & ToSdkLang)
    //   - Services/MeetingTranslationCoordinator.cs (GetTranslatingIndicator)
    //   - Services/ai-translation.cs (LangName, LangStyle, fillers)
    private static readonly HashSet<string> _supportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "en", "ja",
        // "ko", "ms", "th", "vi", "fil", "km", "my", "lo", "ta",
    };

    // Whitelist sisi server: nilai di luar daftar (mis. "auto" atau "ko" dari klien SignalR lain)
    // ditolak dan dikembalikan ke default, bukan diteruskan diam-diam ke engine.
    public static bool IsSupported(string? lang)
        => !string.IsNullOrWhiteSpace(lang) && _supportedLanguages.Contains(lang);
}
}
