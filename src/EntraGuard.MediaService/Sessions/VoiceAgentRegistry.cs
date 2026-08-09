using System.Collections.Concurrent;
using EntraGuard.MediaService.Agents;

namespace EntraGuard.MediaService.Sessions;

/// <summary>
/// Which conversational agent, if any, owns the voice channel of a given verification.
///
/// Exists to settle a question that produced the same bug three times: who is allowed to
/// speak. Two independent controllers had the ability to put audio on the call — the
/// realtime agent through the media stream, and the coordinator through PlayToAll — and
/// nothing arbitrated between them. Every fix that suppressed one case left another, because
/// the problem was never a particular line; it was that speaking was unowned.
///
/// So the rule is now structural: if an agent is registered here, it is the only voice on
/// that call, and the coordinator routes anything it wants said through it. If nothing is
/// registered, the coordinator speaks directly. There is no third state.
/// </summary>
public sealed class VoiceAgentRegistry
{
    private readonly ConcurrentDictionary<string, VoiceAgent> _agents = new();

    public void Register(string verificationId, VoiceAgent agent) => _agents[verificationId] = agent;

    public void Remove(string verificationId) => _agents.TryRemove(verificationId, out _);

    /// <summary>The agent speaking for this verification, or null when nothing is.</summary>
    public VoiceAgent? For(string verificationId) =>
        _agents.TryGetValue(verificationId, out var agent) ? agent : null;
}
