using Teams_Translation_App.Hubs;
using Teams_Translation_App.Services;
using LiveTranslation.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// ── Blazor + SignalR ─────────────────────────────────────────────────────────
// .NET 8 unified model: AddRazorComponents + AddInteractiveServerComponents
// is the ONLY correct way to register Blazor Server in .NET 8.
// Do NOT also call AddServerSideBlazor() — that is the old .NET 6/7 API and
// registering both causes conflicting circuit handlers which crashes /_blazor.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Circuit DetailedErrors — configure via CircuitOptions (not AddServerSideBlazor)
builder.Services.Configure<CircuitOptions>(options =>
{
    options.DetailedErrors = builder.Environment.IsDevelopment();
});

// AddSignalR() is needed for the custom TranslationHub endpoint.
// AddRazorComponents already registers SignalR internally; this call is
// idempotent and only ensures the custom hub is also available.
builder.Services.AddSignalR();

// ── Engine & Services ────────────────────────────────────────────────────────
builder.Services.AddSingleton<AITranslationEngine>();
builder.Services.AddSingleton<MeetingSessionManager>();
builder.Services.AddSingleton<AudioStreamService>();

// ── CORS ─────────────────────────────────────────────────────────────────────
// AllowAnyOrigin() is used here for the bot's audio POST/WebSocket endpoints.
// Blazor circuit (/_blazor) and the SignalR translation hub (/hubs/translation)
// are accessed from the same origin as the page, so CORS does not apply to them.
// Note: AllowAnyOrigin() is intentionally NOT combined with AllowCredentials()
// because they are mutually exclusive; credentials are not required for bot audio.
builder.Services.AddCors(options =>
    options.AddPolicy("BotAudioPolicy", p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

const string TeamsFrameAncestorsDirective =
    "frame-ancestors 'self' https://teams.microsoft.com https://*.teams.microsoft.com https://teams.cloud.microsoft https://*.cloud.microsoft.com https://*.skype.com https://*.microsoft.com https://*.office.com https://*.office365.com https://local.teams.office.com https://*.teams.microsoft.us";

static bool IsTeamsEmbedPath(PathString path) =>
    path.StartsWithSegments("/translation-tab", StringComparison.OrdinalIgnoreCase) ||
    path.StartsWithSegments("/tab-config", StringComparison.OrdinalIgnoreCase);

static string EnforceTeamsFrameAncestorsCsp(string? existingCsp)
{
    var directives = (existingCsp ?? string.Empty)
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(d => !d.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase))
        .ToList();

    directives.Add(TeamsFrameAncestorsDirective);
    return string.Join("; ", directives) + ";";
}

// ── Middleware ────────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// UseWebSockets must be early so WebSocket upgrade requests are recognised
// before routing and other middleware intercept them.
app.UseWebSockets();

app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        if (!IsTeamsEmbedPath(ctx.Request.Path))
            return Task.CompletedTask;

        var cspValues = ctx.Response.Headers.ContentSecurityPolicy;
        var cspValue = string.Join("; ", cspValues.Where(v => !string.IsNullOrWhiteSpace(v)));
        ctx.Response.Headers.ContentSecurityPolicy = EnforceTeamsFrameAncestorsCsp(cspValue);

        // X-Frame-Options with SAMEORIGIN/DENY blocks Teams iframe.
        ctx.Response.Headers.Remove("X-Frame-Options");
        return Task.CompletedTask;
    });
    await next();
});

app.UseStaticFiles();
// CORS middleware must be in the pipeline for per-endpoint RequireCors() to work.
// No default policy is passed here; only endpoints that explicitly call
// RequireCors("BotAudioPolicy") will have CORS headers added.  This prevents
// the Blazor circuit (/_blazor) and the translation hub (/hubs/translation)
// from receiving unexpected CORS responses that can break the WebSocket upgrade.
app.UseCors();
app.UseAntiforgery();

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapHub<TranslationHub>("/hubs/translation");

app.MapPost("/api/audio", async (HttpContext ctx, [FromServices] AudioStreamService svc) =>
    await svc.HandleAudioChunkAsync(ctx)).RequireCors("BotAudioPolicy");

app.MapGet("/ws/audio", async (HttpContext ctx, [FromServices] AudioStreamService svc) =>
    await svc.HandleWebSocketAsync(ctx)).RequireCors("BotAudioPolicy");

// ── Engine start endpoint (production use) ────────────────────────────────
// The engine must NOT be started from a Razor component because the Azure
// Speech SDK loads native DLLs which may not be resolvable from the Blazor
// component's execution context (causes DllNotFoundException).  Instead the
// bot (or a test client) calls this endpoint to start the engine for a given
// meeting, then pushes audio bytes to /api/audio or /ws/audio.
app.MapPost("/api/engine/start", async (
    [FromServices] MeetingSessionManager sessions,
    [FromQuery] string? meetingId) =>
{
    if (string.IsNullOrWhiteSpace(meetingId))
        return Results.BadRequest("meetingId is required");
    try
    {
        var session = sessions.GetOrCreateSession(meetingId);
        if (!session.Engine.IsRunning)
            await session.Engine.StartAsync(MeetingSessionManager.NormalizeMeetingId(meetingId));
        return Results.Ok(new { status = "started", meetingId });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/version", () => Results.Ok(new
{
    version = "1.1.0",
    deployedAt = "2026-04-13"
}));

app.MapGet("/", () => Results.Redirect("/translation-tab"));

app.MapRazorComponents<Teams_Translation_App.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
