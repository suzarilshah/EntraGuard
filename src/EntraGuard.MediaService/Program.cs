using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Azure.Communication.CallAutomation;
using Azure.Communication.Identity;
using Azure.Data.Tables;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Ingestion;
using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Endpoints;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using EntraGuard.MediaService.Sinks;
using EntraGuard.MediaService.Tools;
using EntraGuard.Shared.Policy;
using EntraGuard.MediaService.Persistence;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ───────────────────────────────────────────────────────────
// Bound from the environment variables the Container App injects. No secrets: every
// Azure dependency below authenticates with the user-assigned managed identity.
builder.Services.Configure<EntraGuardOptions>(o =>
{
    var config = builder.Configuration;
    o.AcsEndpoint = config["ACS_ENDPOINT"] ?? string.Empty;
    o.AcsConnectionString = config["ACS_CONNECTION_STRING"] ?? string.Empty;
    o.PublicBaseUrl = (config["PUBLIC_BASE_URL"] ?? string.Empty).TrimEnd('/');
    o.SpeechEndpoint = config["SPEECH_ENDPOINT"] ?? string.Empty;

    // Recognition locale. en-US was the hardcoded default and it is wrong wherever this is
    // actually used: a Malaysian caller answering with the name "Aiman" was transcribed as
    // "Bonne fortune!" on a live call, which the judge then correctly refused. The question
    // was answered; the words never survived the trip.
    o.SpeechLanguage = config["SPEECH_LANGUAGE"] ?? "en-US";
    o.SpeechRegion = config["SPEECH_REGION"] ?? "eastus";
    o.AiServicesEndpoint = config["AI_SERVICES_ENDPOINT"] ?? string.Empty;
    o.OpenAiEndpoint = config["AOAI_ENDPOINT"] ?? string.Empty;
    o.OpenAiDeployment = config["AOAI_DEPLOYMENT"] ?? "entraguard-analyst";
    o.OpenAiApiVersion = config["AOAI_API_VERSION"] ?? "2024-12-01-preview";
    o.DceEndpoint = config["DCE_ENDPOINT"] ?? string.Empty;
    o.DcrImmutableId = config["DCR_IMMUTABLE_ID"] ?? string.Empty;
    o.StorageAccountName = config["STORAGE_ACCOUNT_NAME"] ?? string.Empty;
    o.RealtimeEndpoint = config["AOAI_REALTIME_ENDPOINT"] ?? string.Empty;
    o.RealtimeDeployment = config["AOAI_REALTIME_DEPLOYMENT"] ?? string.Empty;
    o.ServiceClientId = config["ENTRA_SERVICE_CLIENT_ID"] ?? string.Empty;
    o.HomeTenantId = config["AZURE_TENANT_ID"] ?? string.Empty;
    o.VoiceprintUrl = config["VOICEPRINT_URL"] ?? string.Empty;
    o.VoiceEnforce = string.Equals(config["VOICE_MODE"], "enforce", StringComparison.OrdinalIgnoreCase);
    o.RpClientId = config["ENTRA_RP_CLIENT_ID"] ?? string.Empty;
    o.VoiceprintKey = config["VOICEPRINT_KEY"] ?? string.Empty;
    o.RequireMfaForEnrollment = !string.Equals(config["VOICE_REQUIRE_MFA"], "false", StringComparison.OrdinalIgnoreCase);
    if (double.TryParse(config["VOICE_ACCEPT"], out var accept)) o.VoiceAcceptThreshold = accept;
    if (double.TryParse(config["VOICE_REJECT"], out var reject)) o.VoiceRejectThreshold = reject;
    o.QuarantineGroupId = config["ENTRA_QUARANTINE_GROUP_ID"] ?? string.Empty;

    // Resolved once by scripts/00-preflight.sh. Defaulting to Degraded means an
    // unconfigured deployment under-claims rather than promising risk elevation it
    // cannot deliver.
    o.RiskTier = string.Equals(config["ENTRAGUARD_RISK_TIER"], "graph", StringComparison.OrdinalIgnoreCase)
        ? TenantRiskTier.Graph
        : TenantRiskTier.Degraded;

    o.AutonomousActionsEnabled =
        !string.Equals(config["ENTRAGUARD_SHADOW_MODE"], "true", StringComparison.OrdinalIgnoreCase);
});

// ── Identity ────────────────────────────────────────────────────────────────
// AZURE_CLIENT_ID selects the user-assigned identity in Azure; locally this falls through
// to the developer's az login.
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"],
});
builder.Services.AddSingleton<TokenCredential>(credential);

// ── Azure clients ───────────────────────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config["ACS_CONNECTION_STRING"];

    // Local dev may only have a connection string; Azure always uses the managed identity.
    return string.IsNullOrEmpty(connectionString)
        ? new CallAutomationClient(new Uri(config["ACS_ENDPOINT"]!), credential)
        : new CallAutomationClient(connectionString);
});

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new LogsIngestionClient(new Uri(config["DCE_ENDPOINT"]!), credential);
});

// Mints the ACS identities and VoIP tokens the browser soft-phone needs in order to be
// callable. Managed identity, same as everything else.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config["ACS_CONNECTION_STRING"];
    return string.IsNullOrEmpty(connectionString)
        ? new CommunicationIdentityClient(new Uri(config["ACS_ENDPOINT"]!), credential)
        : new CommunicationIdentityClient(connectionString);
});

// Persists the Entra-to-ACS identity mapping. Without it a returning user gets a new ACS
// identity each sign-in and the verification call rings an endpoint nobody is on.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var account = config["STORAGE_ACCOUNT_NAME"];
    return new TableServiceClient(new Uri($"https://{account}.table.core.windows.net"), credential);
});

// ── EntraGuard services ─────────────────────────────────────────────────────
builder.Services.AddSingleton<LiveCallRegistry>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IStateStore, TableStateStore>();
builder.Services.AddSingleton<RpSessionService>();
builder.Services.AddSingleton<TransportProtection>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<VerificationLedger>();
builder.Services.AddSingleton<GrantService>();
builder.Services.AddSingleton<TenantPolicyService>();
builder.Services.AddSingleton<ReadinessService>();
builder.Services.AddSingleton<StepUpService>();
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddSingleton<PreferenceService>();
builder.Services.AddHostedService<VerificationOutboxWorker>();
builder.Services.AddSingleton<VerificationRegistry>();
builder.Services.AddSingleton<VerificationCoordinator>();
builder.Services.AddSingleton<LogsIngestionSink>();

// Faults: where silent degradation becomes visible. Registered beside the sink because it
// writes through it, and given the sink after the container is built so neither has to know
// about the other's construction order.
builder.Services.AddSingleton<FaultRecorder>();

// Registered singleton: the Table client is created lazily on first use, so a deployment
// with no storage account configured still starts rather than failing at boot over a
// feature nobody has enrolled in yet.
builder.Services.AddSingleton<KnowledgeStore>();
builder.Services.AddSingleton<VoiceAgentRegistry>();
builder.Services.AddSingleton<TelemetryChallenge>();
builder.Services.AddSingleton<ProfileChallenge>();
builder.Services.AddSingleton<CrossTenantGraph>();
builder.Services.AddSingleton<AnalystAgent>();
builder.Services.AddSingleton<ActuatorAgent>();

// Named clients, not typed clients. The remediation tools below are singletons, and a
// typed HttpClient injected into a singleton pins one message handler forever — defeating
// handler rotation and leaving the connection pool on stale DNS in a long-lived service.
builder.Services.AddHttpClient(GraphClient.ClientName, client =>
{
    client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
    // Remediation runs while a call is live. A Graph call that hangs must give up quickly
    // enough for the next rung of the ladder to still be useful.
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient(RaiseSentinelIncidentTool.ClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

// The Analyst talks to Azure OpenAI over REST rather than through the SDK; see
// AnalystClient for why. 30s is a ceiling, not a target — a verdict that slow has already
// missed the window, and the timeout exists so a hung request cannot stall the loop.
builder.Services.AddHttpClient(AnalystClient.ClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<AnalystClient>();

builder.Services.AddHttpClient(KnowledgeJudge.ClientName, client =>
{
    // Tight, because a live caller is waiting in silence while this runs. If the judge
    // cannot answer in this window the hash result stands, which is the safe direction.
    client.Timeout = TimeSpan.FromSeconds(12);
});
builder.Services.AddSingleton<KnowledgeJudge>();

builder.Services.AddHttpClient(VoiceprintClient.ClientName, client =>
{
    // Embedding a few seconds of audio on CPU takes well under a second; this ceiling is
    // for a wedged or still-loading replica. A live caller is waiting, and voice failing
    // to score is survivable in a way that a stalled call is not.
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<VoiceprintClient>();
builder.Services.AddSingleton<VoiceprintStore>();
builder.Services.AddSingleton<VoiceEnrollmentCoordinator>();
builder.Services.AddSingleton<VoiceCalibration>();
builder.Services.AddSingleton<EnrollmentRehearsal>();
builder.Services.AddSingleton<AcsEnrollmentSmokeTest>();

// Authentication exists ONLY for the voice-profile endpoints. It is added unconditionally
// so the policy is always registered — the endpoints themselves refuse when no client id is
// configured, rather than silently becoming anonymous.
builder.Services.AddSingleton<MfaEvidence>();
builder.Services.AddVoiceProfileAuth(builder.Configuration["ENTRA_RP_CLIENT_ID"] ?? "unset");
builder.Services.AddAuthentication(o =>
{
    o.DefaultAuthenticateScheme = "EntraGuardRequest";
    o.DefaultChallengeScheme = "EntraGuardRequest";
})
    .AddPolicyScheme("EntraGuardRequest", "Bearer or revocable session", o => o.ForwardDefaultSelector = context =>
        context.Request.Headers.ContainsKey(RpSessionService.Header) ? SessionAuthenticationHandler.SchemeName : VoiceProfileAuth.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddSingleton<GraphClient>();
builder.Services.AddSingleton<RaiseSentinelIncidentTool>();

// The remediation tool catalogue. Registered as a collection so the Actuator resolves
// tools by action, and so adding a capability means adding one class here rather than
// editing a dispatch switch.
builder.Services.AddSingleton<IRemediationTool, LogTelemetryTool>();
builder.Services.AddSingleton<IRemediationTool, NotifySocTool>();
builder.Services.AddSingleton<IRemediationTool, InjectVoiceWarningTool>();
builder.Services.AddSingleton<IRemediationTool, RevokeSessionsTool>();
builder.Services.AddSingleton<IRemediationTool, QuarantineUserTool>();
builder.Services.AddSingleton<IRemediationTool, ElevateUserRiskTool>();
builder.Services.AddSingleton<IRemediationTool, TerminateCallTool>();
builder.Services.AddSingleton<IRemediationTool>(sp =>
    sp.GetRequiredService<RaiseSentinelIncidentTool>());

builder.Services.AddSignalR();

// Origins are allow-listed when ALLOWED_ORIGINS is set, and only fall back to "anything"
// when it is not — which is the local-development case, where the portal runs on a port
// that changes. AllowCredentials with a wildcard origin is the combination that lets any
// page on the internet make credentialed calls to this service on a visitor's behalf.
var allowedOrigins = (builder.Configuration["ALLOWED_ORIGINS"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins);
    }
    else
    {
        policy.WithOrigins("http://localhost:3000");
    }

    policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// Rate limiting.
//
// Nothing bounded how many verification calls could be started against one person. Each one
// rings their phone, so an unbounded loop here is MFA fatigue delivered by telephone — the
// exact social-engineering pressure this product exists to detect, available as an anonymous
// HTTP request. Enrolment is capped harder still: each attempt places a real call and writes
// a biometric.
//
// Partitioned by UPN where the body carries one and by remote IP otherwise, because
// per-IP-only limits punish everyone behind one corporate NAT for one abuser.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("verification-start", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
            }));

    options.AddPolicy("enrollment-start", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromHours(1),
            }));

    static string RateLimitKey(HttpContext context) =>
        context.User.FindFirst("oid")?.Value
        ?? context.Request.Headers["X-Forwarded-For"].ToString()
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";
});

if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

var app = builder.Build();

// Give the recorder its sink now the container exists. Set here rather than injected so the
// recorder can be constructed by anything, including code paths that have no telemetry.
var faultRecorder = app.Services.GetRequiredService<FaultRecorder>();
var ingestionSink = app.Services.GetRequiredService<LogsIngestionSink>();

faultRecorder.Sink = ingestionSink;
ingestionSink.Faults = faultRecorder;
app.Services.GetRequiredService<VoiceprintClient>().Faults = faultRecorder;

app.UseRouting();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiAccessMiddleware>();
app.UseRateLimiter();

app.UseWebSockets(new WebSocketOptions
{
    // ACS holds the media socket open for the whole call. The default 2-minute keep-alive
    // is fine, but the interval is set explicitly so a long quiet stretch mid-call — which
    // is exactly when a victim is listening to an attacker talk — cannot look like a dead
    // connection to an intermediary.
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

app.MapIncomingCall();
app.MapRpSessions();
app.MapAccount();
app.MapCallbacks();
app.MapMediaSocket();
app.MapSessionApi();
app.MapSimulation();
app.MapAcsIdentity();
app.MapPresence();

// Read-only, on no call path, and additive: nothing here can affect a verification.
app.MapDiagnostics();

app.MapVerification();
app.MapVoiceProfile();
app.MapVoiceEnrollment();
app.MapVerificationSimulation();
app.MapHub<LiveHub>("/hubs/live", o => o.CloseOnAuthenticationExpiration = true);

app.Logger.LogInformation(
    "EntraGuard media service starting. Public base URL: {BaseUrl}. Risk tier: {RiskTier}.",
    builder.Configuration["PUBLIC_BASE_URL"],
    builder.Configuration["ENTRAGUARD_RISK_TIER"] ?? "degraded");

app.Run();
