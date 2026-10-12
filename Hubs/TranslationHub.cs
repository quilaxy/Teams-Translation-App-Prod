using Microsoft.AspNetCore.SignalR;
using Teams_Translation_App.Services;
using LiveTranslation.Engine;

namespace Teams_Translation_App.Hubs
{
    public class TranslationHub : Hub
    {
        private readonly MeetingSessionManager _sessionManager;

        public TranslationHub(MeetingSessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }

        // Client (browser) memanggil ini saat halaman pertama kali load
        // untuk masuk ke group berdasarkan meetingId
        public async Task JoinMeeting(string meetingId)
        {
            var safeMeetingId = MeetingSessionManager.NormalizeMeetingId(meetingId);
            await Groups.AddToGroupAsync(Context.ConnectionId, safeMeetingId);
            await _sessionManager.JoinMeetingAsync(Context.ConnectionId, safeMeetingId);
            if (_sessionManager.TryGetSession(safeMeetingId, out var session))
            {
                await Clients.Caller.SendAsync(
                    "EngineState",
                    session.Engine.IsRunning,
                    session.Engine.Mode.ToString());
            }
            Console.WriteLine($"[HUB] {Context.ConnectionId} joined meeting: {safeMeetingId}");
        }

        public async Task LeaveMeeting(string meetingId)
        {
            var safeMeetingId = MeetingSessionManager.NormalizeMeetingId(meetingId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, safeMeetingId);
            await _sessionManager.LeaveMeetingAsync(Context.ConnectionId, safeMeetingId);
            Console.WriteLine($"[HUB] {Context.ConnectionId} left meeting: {safeMeetingId}");
        }

        public async Task SetInputLanguage(string lang)
        {
            await _sessionManager.SetInputLanguageAsync(Context.ConnectionId, lang);
        }

        public async Task SetOutputLanguage(string lang)
        {
            await _sessionManager.SetOutputLanguageAsync(Context.ConnectionId, lang);
        }

        public async Task StartEngine(string meetingId)
        {
            var safeMeetingId = MeetingSessionManager.NormalizeMeetingId(meetingId);
            var session = _sessionManager.GetOrCreateSession(safeMeetingId);
            if (!session.Engine.IsRunning)
                await session.Engine.StartAsync(safeMeetingId);
        }

        public async Task StopEngine(string meetingId)
        {
            var safeMeetingId = MeetingSessionManager.NormalizeMeetingId(meetingId);
            if (!_sessionManager.TryGetSession(safeMeetingId, out var session))
                return;
            if (session.Engine.IsRunning)
                await session.Engine.StopAsync();
        }

        public async Task SwitchMode(string meetingId, string mode)
        {
            var safeMeetingId = MeetingSessionManager.NormalizeMeetingId(meetingId);
            if (!_sessionManager.TryGetSession(safeMeetingId, out var session))
                return;

            if (!Enum.TryParse<TranslationMode>(mode, ignoreCase: true, out var parsedMode))
                return;
            await session.Engine.SwitchModeAsync(parsedMode);
            await Clients.Group(safeMeetingId).SendAsync("EngineModeChanged", parsedMode.ToString());
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            Console.WriteLine($"[HUB] Disconnected: {Context.ConnectionId}");
            await _sessionManager.RemoveConnectionAsync(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
