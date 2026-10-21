namespace Teams_Translation_App.Services
{
public static class TranslationDefaults
{
    // Default hanyalah pilihan awal yang tampil di UI (dan nilai awal engine/koordinator).
    // User bebas mengganti bahasa kapan saja. Satu sumber untuk UI, koordinator, dan engine.
    public const string DefaultInputLanguage = "id";
    public const string DefaultOutputLanguage = "ja";
}
}
