# Teams Translation App (KID AI Live Translation)

Aplikasi ini adalah **Blazor Server + SignalR** untuk menampilkan caption terjemahan real-time di Microsoft Teams meeting. Audio dikirim oleh bot (atau client eksternal) ke server, lalu hasil STT/terjemahan dipancarkan ke tab meeting via SignalR.

## Fitur utama
- Terjemahan real-time ke panel meeting Teams (side panel) dan mode popout.
- Pilihan bahasa input/output per pengguna (per koneksi SignalR), bukan global meeting.
- Dua mode terjemahan: `Direct` (Azure Speech Translation) dan `SttOpenAI`.
- Streaming audio via HTTP POST atau WebSocket.
- Manajemen sesi per meeting dengan auto-cleanup saat idle.

## Arsitektur singkat
- **Blazor Server**: UI di `/translation-tab` dan konfigurasi tab di `/tab-config`.
- **SignalR Hub**: `/hubs/translation` untuk broadcast caption dan status engine.
- **MeetingSessionManager**: mengelola sesi per meeting dan routing per koneksi.
- **LiveTranslationEngine**: STT + translation (Azure Speech) + optional OpenAI.
- **AudioStreamService**: menerima audio dari bot dan mendorong ke engine.

## Prasyarat
- .NET 8 SDK
- Azure Speech resource
- (Opsional) Azure OpenAI untuk mode `SttOpenAI`

## Konfigurasi
Aplikasi membaca konfigurasi dari `IConfiguration` (appsettings.json atau environment variables).

**Wajib (Azure Speech):**
- `Speech:Key`
- `Speech:Region`

**Opsional (Azure OpenAI, untuk mode `SttOpenAI`):**
- `AzureOpenAI:Endpoint`
- `AzureOpenAI:Key`
- `AzureOpenAI:Deployment`

Contoh environment variables:
- `Speech__Key`
- `Speech__Region`
- `AzureOpenAI__Endpoint`
- `AzureOpenAI__Key`
- `AzureOpenAI__Deployment`

## Menjalankan secara lokal
```bash
dotnet build
dotnet run
```
Buka:
- `https://localhost:<port>/translation-tab`
- `https://localhost:<port>/tab-config`

Query string yang didukung:
- `meetingId` (default: `default`)
- `inputLang` (default: `id`; saat ini hanya `id`, `en`, `ja`)
- `outputLang` (default: `en`; saat ini hanya `id`, `en`, `ja`)
- `popout=true` (mode popout)

Catatan bahasa: saat ini hanya `id`, `en`, `ja` yang aktif (default input `id`, output `en`; auto detect sudah dihapus).
Bahasa lain (ko, ms, th, vi, fil, km, my, lo, ta) di-comment, bukan dihapus, dan ditandai `[FUTURE-LANG]`.
Untuk mengaktifkannya lagi: `grep -rn "FUTURE-LANG" --include=*.cs --include=*.razor .` lalu un-comment semua tempatnya.

## Integrasi bot / streaming audio
1) **Start engine**
```
POST /api/engine/start?meetingId=<meetingId>
```

2) **Streaming audio (HTTP POST)**
```
POST /api/audio?meetingId=<meetingId>
```
Body berisi bytes audio **PCM raw**.

3) **Streaming audio (WebSocket)**
```
WS /ws/audio?meetingId=<meetingId>
```

**Format audio yang didukung:**
- PCM raw (bukan MP3/Opus)
- 16kHz, 16-bit, mono
- Batas chunk 64 KB

## Endpoint tambahan
- `GET /version`

## Teams App package
File manifest ada di:
```
TeamsApp/manifest.json
```
Untuk packaging:
1. Pastikan `contentUrl`, `configurationUrl`, dan `validDomains` sesuai domain deployment.
2. Zip `manifest.json` + `color.png` + `outline.png`.
3. Upload package ke Teams.

## Validasi build
```bash
dotnet build
```
