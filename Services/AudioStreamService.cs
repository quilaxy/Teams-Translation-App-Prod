using Microsoft.CognitiveServices.Speech.Audio;

namespace Teams_Translation_App.Services
{
    // Service ini menjadi "jembatan" antara bot teman kamu dan LiveTranslationEngine.
    // Bot mengirim audio PCM 16kHz 16bit mono via POST /api/audio
    // Service ini meneruskan bytes tersebut ke PushAudioInputStream engine.
    //
    // Format audio yang HARUS dikirim bot:
    //   - PCM raw (bukan MP3/Opus)
    //   - Sample rate : 16000 Hz
    //   - Bit depth   : 16 bit
    //   - Channels    : 1 (mono)
    //   Ini sama persis dengan format yang dulu di-resample oleh NAudio di WinForms.

    public class AudioStreamService
    {
        private const int MaxChunkBytes = 64 * 1024;
        private static bool IsChunkTooLarge(int size) => size > MaxChunkBytes;
        private readonly MeetingSessionManager _sessions;
        private readonly ILogger<AudioStreamService> _logger;

        public AudioStreamService(
            MeetingSessionManager sessions,
            ILogger<AudioStreamService> logger)
        {
            _sessions = sessions;
            _logger = logger;
        }

        // Dipanggil dari endpoint POST /api/audio
        // Bot POST raw PCM bytes ke endpoint ini
        public async Task HandleAudioChunkAsync(HttpContext context)
        {
            var meetingIdRaw = context.Request.Query["meetingId"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(meetingIdRaw))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("meetingId is required");
                return;
            }

            var meetingId = MeetingSessionManager.NormalizeMeetingId(meetingIdRaw);
            if (!_sessions.TryGetSession(meetingId, out var session))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Engine session not started");
                return;
            }

            try
            {
                // Baca body request sebagai raw bytes
                using var ms = new MemoryStream();
                await context.Request.Body.CopyToAsync(ms);
                var audioBytes = ms.ToArray();

                if (audioBytes.Length > 0)
                {
                    if (IsChunkTooLarge(audioBytes.Length))
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        await context.Response.WriteAsync("Audio chunk too large");
                        return;
                    }

                    if (!session.TryReserveAudioBuffer(audioBytes.Length))
                    {
                        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                        await context.Response.WriteAsync("Audio buffer full for meeting");
                        return;
                    }

                    await session.AudioWriteLock.WaitAsync(context.RequestAborted);
                    try
                    {
                        var mode = session.Engine.Mode;
                        if (mode == LiveTranslation.Engine.TranslationMode.Direct)
                        {
                            session.Engine.PushStreamA?.Write(audioBytes, audioBytes.Length);
                        }
                        else
                        {
                            session.Engine.PushStreamB?.Write(audioBytes, audioBytes.Length);
                        }
                        session.Touch();
                        _logger.LogDebug("[AUDIO] Meeting={MeetingId} Received {Bytes} bytes", meetingId, audioBytes.Length);
                    }
                    finally
                    {
                        session.AudioWriteLock.Release();
                        session.ReleaseAudioBuffer(audioBytes.Length);
                    }
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AUDIO] Error processing chunk meeting={MeetingId}", meetingId);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }

        // Versi WebSocket — kalau bot teman kamu pakai WebSocket streaming
        // (lebih efisien daripada HTTP POST per chunk)
        public async Task HandleWebSocketAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var meetingIdRaw = context.Request.Query["meetingId"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(meetingIdRaw))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var meetingId = MeetingSessionManager.NormalizeMeetingId(meetingIdRaw);
            if (!_sessions.TryGetSession(meetingId, out var session))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var ws  = await context.WebSockets.AcceptWebSocketAsync();
            var buffer    = new byte[4096];
            _logger.LogInformation("[AUDIO-WS] Bot connected meeting={MeetingId}", meetingId);

            try
            {
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        context.RequestAborted);

                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(
                            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                            "Closed by bot", context.RequestAborted);
                        break;
                    }

                    if (result.Count > 0)
                    {
                        if (IsChunkTooLarge(result.Count))
                        {
                            await ws.CloseAsync(
                                System.Net.WebSockets.WebSocketCloseStatus.MessageTooBig,
                                "chunk too large", context.RequestAborted);
                            break;
                        }

                        if (!session.TryReserveAudioBuffer(result.Count))
                        {
                            await ws.CloseAsync(
                                System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                                "meeting buffer full", context.RequestAborted);
                            break;
                        }

                        await session.AudioWriteLock.WaitAsync(context.RequestAborted);
                        try
                        {
                            var mode = session.Engine.Mode;
                            if (mode == LiveTranslation.Engine.TranslationMode.Direct)
                                session.Engine.PushStreamA?.Write(buffer, result.Count);
                            else
                                session.Engine.PushStreamB?.Write(buffer, result.Count);
                            session.Touch();
                        }
                        finally
                        {
                            session.AudioWriteLock.Release();
                            session.ReleaseAudioBuffer(result.Count);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "[AUDIO-WS] Error meeting={MeetingId}", meetingId); }
            finally { _logger.LogInformation("[AUDIO-WS] Bot disconnected meeting={MeetingId}", meetingId); }
        }
    }
}
