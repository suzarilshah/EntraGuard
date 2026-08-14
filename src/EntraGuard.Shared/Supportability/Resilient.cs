namespace EntraGuard.Shared.Supportability;

/// <summary>
/// Retry with backoff, and a circuit that opens when a dependency is genuinely down.
///
/// <para>
/// Deliberately small and dependency-free. The alternative was a resilience library, and it
/// would have brought policy registries, decorators and a configuration surface to solve a
/// problem this system has in exactly four places. What follows fits on a screen, which
/// matters more here than generality: this code runs while somebody is on a phone call, and
/// anybody debugging it at that moment needs to be able to read all of it.
/// </para>
///
/// <para>
/// The self-healing property is the circuit, not the retry. Retrying is how you survive a
/// dropped packet. Shedding calls to a service that has failed five times running is how you
/// stop a dead dependency from turning every subsequent call into a timeout — and reopening
/// automatically, on a probe, is what makes recovery need no human. Nothing here restarts,
/// redeploys or reconfigures anything: self-healing that takes actions of that size on a
/// system holding live authentication calls would be a worse failure mode than the one it
/// was written to fix.
/// </para>
/// </summary>
public sealed class Resilient(
    string name,
    int failuresBeforeOpen = 5,
    TimeSpan? openFor = null,
    int maxAttempts = 3)
{
    private readonly TimeSpan _openFor = openFor ?? TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;

    /// <summary>The dependency this guards, for messages.</summary>
    public string Name { get; } = name;

    /// <summary>True while calls are being shed.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveFailures >= failuresBeforeOpen
                    && DateTimeOffset.UtcNow - _openedAt < _openFor;
            }
        }
    }

    /// <summary>Consecutive failures, for diagnostics.</summary>
    public int ConsecutiveFailures
    {
        get { lock (_gate) { return _consecutiveFailures; } }
    }

    /// <summary>Why the last attempt failed, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>When the last attempt succeeded.</summary>
    public DateTimeOffset? LastSuccess { get; private set; }

    /// <summary>
    /// Run <paramref name="operation"/>, retrying transient failures.
    /// </summary>
    /// <param name="onShed">
    /// Called instead of the operation when the circuit is open — the caller's chance to
    /// record a fault, so shedding is visible rather than merely quiet.
    /// </param>
    /// <returns>
    /// The result, or <paramref name="fallback"/> when every attempt failed or the circuit
    /// was open. Never throws: callers of this class are on a live call path and a
    /// supportability mechanism that can abort one is not an improvement.
    /// </returns>
    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        T fallback,
        CancellationToken cancellationToken = default,
        Action<string>? onShed = null)
    {
        if (IsOpen)
        {
            onShed?.Invoke($"{Name} circuit open after {ConsecutiveFailures} consecutive failures: {LastError}");
            return fallback;
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await operation(cancellationToken);

                lock (_gate)
                {
                    _consecutiveFailures = 0;
                }

                LastSuccess = DateTimeOffset.UtcNow;
                LastError = null;
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The call ended. Not a dependency failure, and counting it as one would open
                // the circuit on a service that is perfectly healthy.
                throw;
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";

                if (attempt == maxAttempts)
                {
                    lock (_gate)
                    {
                        _consecutiveFailures++;

                        if (_consecutiveFailures == failuresBeforeOpen)
                        {
                            _openedAt = DateTimeOffset.UtcNow;
                        }
                    }

                    return fallback;
                }

                // Exponential, with jitter. Without jitter every caller that failed together
                // retries together, and the recovering dependency is hit by exactly the
                // synchronised burst it just fell over under.
                var backoff = TimeSpan.FromMilliseconds(
                    200 * Math.Pow(2, attempt - 1) * (0.5 + Random.Shared.NextDouble()));

                await Task.Delay(backoff, cancellationToken);
            }
        }

        return fallback;
    }

    /// <summary>A snapshot for the diagnostics endpoint.</summary>
    public object Describe() => new
    {
        name = Name,
        healthy = !IsOpen && ConsecutiveFailures == 0,
        circuitOpen = IsOpen,
        consecutiveFailures = ConsecutiveFailures,
        lastSuccess = LastSuccess,
        lastError = LastError,
    };
}
