using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace EntraGuard.MediaService.Sinks;

/// <summary>ACS query capabilities and SignalR access tokens must not enter request telemetry.</summary>
public sealed class RedactRequestQuery(ITelemetryProcessor next) : ITelemetryProcessor
{
    public void Process(ITelemetry item)
    {
        if (item is RequestTelemetry request && request.Url is not null)
            request.Url = new Uri(request.Url.GetLeftPart(UriPartial.Path));
        next.Process(item);
    }
}
