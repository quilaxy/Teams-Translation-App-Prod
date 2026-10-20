using System.Collections.Concurrent;
using System.Linq;
using LiveTranslation.AI;
using LiveTranslation.Engine;
using Microsoft.AspNetCore.SignalR;
using Teams_Translation_App.Hubs;

namespace Teams_Translation_App.Services
{
    public sealed class MeetingSessionManager : IDisposable
    {
        private const string DefaultMeetingId = "default";
        private static readonly TimeSpan IdleSessionTtl = TimeSpan.FromMinutes(15);

        private readonly IHubContext<TranslationHub> _hub;
        private readonly IConfiguration _config;
        private readonly AITranslationEngine _ai;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<MeetingSessionManager> _logger;
        private readonly ConcurrentDictionary<string, MeetingSession> _sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _connectionMeeting = new(StringComparer.Ordinal);
        private readonly Timer _cleanupTimer;

        public MeetingSessionManager(
            IHubContext<TranslationHub> hub,
            IConfiguration config,
            AITranslationEngine ai,
            ILoggerFactory loggerFactory,
            ILogger<MeetingSessionManager> logger)
        {
            _hub = hub;
            _config = config;
            _ai = ai;
            _loggerFactory = loggerFactory;
            _logger = logger;
            _cleanupTimer = new Timer(_ =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CleanupIdleSessionsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[SESSION] Cleanup failed");
                    }
                });
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public static string NormalizeMeetingId(string? meetingId)
        {
            if (string.IsNullOrWhiteSpace(meetingId))
                return DefaultMeetingId;

            var normalized = new string(meetingId
                .Trim()
                .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')
                .ToArray());

            return string.IsNullOrWhiteSpace(normalized) ? DefaultMeetingId : normalized;
        }

        public MeetingSession GetOrCreateSession(string? meetingId)
        {
            var safeMeetingId = NormalizeMeetingId(meetingId);
            var session = _sessions.GetOrAdd(safeMeetingId, CreateSession);
            session.Touch();
            return session;
        }

        public bool TryGetSession(string? meetingId, out MeetingSession session)
        {
            var safeMeetingId = NormalizeMeetingId(meetingId);
            if (_sessions.TryGetValue(safeMeetingId, out var value))
            {
                value.Touch();
                session = value;
                return true;
            }

            session = null!;
            return false;
        }

        public async Task JoinMeetingAsync(string connectionId, string meetingId)
        {
            var safeMeetingId = NormalizeMeetingId(meetingId);
            if (_connectionMeeting.TryGetValue(connectionId, out var previousMeetingId) &&
                !string.Equals(previousMeetingId, safeMeetingId, StringComparison.Ordinal))
            {
                if (_sessions.TryGetValue(previousMeetingId, out var previousSession))
                    await previousSession.Coordinator.LeaveMeetingAsync(connectionId);
            }

            var session = GetOrCreateSession(safeMeetingId);
            _connectionMeeting[connectionId] = safeMeetingId;
            await session.Coordinator.JoinMeetingAsync(connectionId, safeMeetingId);
        }

        public async Task LeaveMeetingAsync(string connectionId, string? meetingId = null)
        {
            string? effectiveMeetingId = null;
            if (!string.IsNullOrWhiteSpace(meetingId))
                effectiveMeetingId = NormalizeMeetingId(meetingId);
            else if (_connectionMeeting.TryGetValue(connectionId, out var mappedMeetingId))
                effectiveMeetingId = mappedMeetingId;

            if (string.IsNullOrWhiteSpace(effectiveMeetingId))
                return;

            if (_sessions.TryGetValue(effectiveMeetingId, out var session))
            {
                await session.Coordinator.LeaveMeetingAsync(connectionId);
                session.Touch();
            }

            _connectionMeeting.TryRemove(connectionId, out _);
        }

        public async Task RemoveConnectionAsync(string connectionId)
            => await LeaveMeetingAsync(connectionId);

        public async Task SetInputLanguageAsync(string connectionId, string lang)
        {
            if (!_connectionMeeting.TryGetValue(connectionId, out var meetingId)) return;
            if (!_sessions.TryGetValue(meetingId, out var session)) return;
            session.Touch();
            await session.Coordinator.SetInputLanguageAsync(connectionId, lang);
        }

        public async Task SetOutputLanguageAsync(string connectionId, string lang)
        {
            if (!_connectionMeeting.TryGetValue(connectionId, out var meetingId)) return;
            if (!_sessions.TryGetValue(meetingId, out var session)) return;
            session.Touch();
            await session.Coordinator.SetOutputLanguageAsync(connectionId, lang);
        }

        private MeetingSession CreateSession(string meetingId)
        {
            var engine = new LiveTranslationEngine(_hub, _config, _ai);
            var coordinatorLogger = _loggerFactory.CreateLogger<MeetingTranslationCoordinator>();
            var coordinator = new MeetingTranslationCoordinator(engine, _ai, _hub, coordinatorLogger);
            var sessionLogger = _loggerFactory.CreateLogger<MeetingSession>();
            var session = new MeetingSession(meetingId, engine, coordinator, sessionLogger);

            engine.OnError += error =>
            {
                _ = PublishMeetingEventSafeAsync(meetingId, "EngineError", error);
            };

            _logger.LogInformation("[SESSION] Created meeting session {MeetingId}", meetingId);
            return session;
        }

        private async Task PublishMeetingEventSafeAsync(string meetingId, string eventName, string payload)
        {
            try
            {
                await _hub.Clients.Group(meetingId).SendAsync(eventName, payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SESSION] Failed publishing {EventName} for {MeetingId}", eventName, meetingId);
            }
        }

        private async Task CleanupIdleSessionsAsync()
        {
            var now = DateTime.UtcNow;
            foreach (var item in _sessions)
            {
                var session = item.Value;
                if (session.Engine.IsRunning) continue;
                if (session.ConnectionCount > 0) continue;
                if (now - session.LastTouchedUtc < IdleSessionTtl) continue;

                if (_sessions.TryRemove(item.Key, out var removed))
                {
                    _logger.LogInformation("[SESSION] Disposing idle session {MeetingId}", removed.MeetingId);
                    await removed.DisposeAsync();
                }
            }
        }

        public void Dispose()
        {
            _cleanupTimer.Dispose();
            foreach (var item in _sessions.Values)
            {
                item.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            _sessions.Clear();
            _connectionMeeting.Clear();
        }
    }

    public sealed class MeetingSession : IAsyncDisposable
    {
        private const int MaxBufferedAudioBytes = 1_000_000;
        private long _lastTouchedTicks = DateTime.UtcNow.Ticks;
        private int _bufferedAudioBytes;

        public MeetingSession(
            string meetingId,
            LiveTranslationEngine engine,
            MeetingTranslationCoordinator coordinator,
            ILogger<MeetingSession> logger)
        {
            MeetingId = meetingId;
            Engine = engine;
            Coordinator = coordinator;
            Logger = logger;
        }

        public string MeetingId { get; }
        public LiveTranslationEngine Engine { get; }
        public MeetingTranslationCoordinator Coordinator { get; }
        public ILogger<MeetingSession> Logger { get; }
        public SemaphoreSlim AudioWriteLock { get; } = new(1, 1);
        public DateTime LastTouchedUtc => new(Interlocked.Read(ref _lastTouchedTicks), DateTimeKind.Utc);
        public int ConnectionCount => Coordinator.ConnectionCount;

        public void Touch()
            => Interlocked.Exchange(ref _lastTouchedTicks, DateTime.UtcNow.Ticks);

        public bool TryReserveAudioBuffer(int bytes)
        {
            while (true)
            {
                var current = Volatile.Read(ref _bufferedAudioBytes);
                var next = current + bytes;
                if (next > MaxBufferedAudioBytes)
                    return false;
                if (Interlocked.CompareExchange(ref _bufferedAudioBytes, next, current) == current)
                    return true;
            }
        }

        public void ReleaseAudioBuffer(int bytes)
            => Interlocked.Add(ref _bufferedAudioBytes, -bytes);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Engine.IsRunning)
                    await Engine.StopAsync();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[SESSION] Failed stopping engine for {MeetingId}", MeetingId);
            }

            Coordinator.Dispose();
            Engine.Dispose();
            AudioWriteLock.Dispose();
        }
    }
}
