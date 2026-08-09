using Azure.Communication.CallAutomation;
using Azure.Communication.Identity;
using Azure.Data.Tables;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Ingestion;
using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Endpoints;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using EntraGuard.MediaService.Sinks;
using EntraGuard.MediaService.Tools;
using EntraGuard.Shared.Policy;

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
builder.Services.AddSingleton<VerificationRegistry>();
builder.Services.AddSingleton<VerificationCoordinator>();
builder.Services.AddSingleton<LogsIngestionSink>();

// Registered singleton: the Table client is created lazily on first use, so a deployment
// with no storage account configured still starts rather than failing at boot over a
// feature nobody has enrolled in yet.
builder.Services.AddSingleton<KnowledgeStore>();
builder.Services.AddSingleton<VoiceAgentRegistry>();
builder.Services.AddSingleton<TelemetryChallenge>();
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

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddApplicationInsightsTelemetry();
}

var app = builder.Build();

app.UseCors();

app.UseWebSockets(new WebSocketOptions
{
    // ACS holds the media socket open for the whole call. The default 2-minute keep-alive
    // is fine, but the interval is set explicitly so a long quiet stretch mid-call — which
    // is exactly when a victim is listening to an attacker talk — cannot look like a dead
    // connection to an intermediary.
    KeepAliveInterval = TimeSpan.FromSeconds(30),
});

app.MapIncomingCall();
app.MapCallbacks();
app.MapMediaSocket();
app.MapSessionApi();
app.MapSimulation();
app.MapAcsIdentity();
app.MapPresence();
app.MapVerification();
app.MapVerificationSimulation();
app.MapHub<LiveHub>("/hubs/live");

app.Logger.LogInformation(
    "EntraGuard media service starting. Public base URL: {BaseUrl}. Risk tier: {RiskTier}.",
    builder.Configuration["PUBLIC_BASE_URL"],
    builder.Configuration["ENTRAGUARD_RISK_TIER"] ?? "degraded");

app.Run();
