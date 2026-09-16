namespace EntraGuard.Shared.Verification;

/// <summary>
/// Turns an ACS disconnect into something the person who saw it can act on.
///
/// This was inline in the disconnect handler, which meant the only way to know what any
/// given failure would say was to re-read the handler — and the only way to test it was to
/// make a real call fail in the right way. Pure and separate, the whole mapping can be
/// exhaustively tested, which is the same reason PolicyGate and VoiceGuardrail are pure.
///
/// <para>
/// The governing rule, inherited from the code this replaces: attach only advice the code
/// actually implies. A 403 means the far end REFUSED; a 487 means it rang and nobody picked
/// up; a 480 means there was nothing to ring. Those have three different fixes, and a
/// diagnostic that guesses sends somebody off to reconfigure a tenant that was working
/// correctly. Saying less is better than saying something wrong.
/// </para>
/// </summary>
public static class CallFailureDiagnosis
{
    /// <param name="neverAnswered">
    /// True when the call never reached CallConnected. "Never rang" and "rang and was
    /// ignored" are different faults with different fixes, and reporting both as a timeout
    /// is what made the earlier failures unreadable.
    /// </param>
    /// <param name="endpointKind">"teams", "phone" or "browser".</param>
    /// <param name="code">ACS result code, when one was reported.</param>
    /// <param name="subCode">ACS sub-code — the part that actually distinguishes causes.</param>
    /// <param name="message">ACS message, kept verbatim because support will ask for it.</param>
    public static string Describe(
        bool neverAnswered, string? endpointKind, int? code, int? subCode, string? message)
    {
        if (!neverAnswered)
        {
            return "The verification call ended before the code was entered.";
        }

        var reason = "The verification call ended before it was answered";
        reason += code is null
            ? "."
            : $" (ACS code {code}/{subCode}: {message}).";

        var teams = string.Equals(endpointKind, "teams", StringComparison.OrdinalIgnoreCase);

        return code switch
        {
            // Refused by the far end. Only Teams has a federation concept, so only Teams
            // gets the federation advice.
            403 when teams =>
                reason + " For a Teams endpoint this means the Teams tenant has not "
                       + "allow-listed this Communication Services resource for ACS "
                       + "federation, or the user is not Enterprise Voice enabled. "
                       + "See docs/teams-setup.md.",

            // Nothing to ring. Measured in the field as 480#10037, "Target user did not have
            // any endpoints registered with ACS", and it arrived with no advice at all —
            // the user saw "Verification failed" and a DiagCode.
            //
            // It is emphatically NOT a federation failure, and saying so matters: reaching
            // 480 means the call got THROUGH to the Teams side and was told nobody was
            // there. A tenant that answers 480 is federated correctly.
            //
            // This is also the failure that disproved "a Teams user is reachable by
            // definition". Microsoft delivers to a locked phone, but only to a device that
            // has registered; an account nobody is signed in to anywhere has nothing to
            // deliver to.
            480 when teams =>
                reason + " Nobody is signed in to Teams as this user, so there was no device "
                       + "to ring. Open Teams as that user, wait until presence shows "
                       + "Available, and start the verification again.",

            480 =>
                reason + " No device was registered to receive the call. Open the EntraGuard "
                       + "page on the phone and tap Connect, or choose this browser and allow "
                       + "the microphone, then start the verification again.",

            // It rang. Entirely different situation, entirely different fix.
            487 => reason + " The call rang and was not answered in time.",

            _ => reason,
        };
    }
}
