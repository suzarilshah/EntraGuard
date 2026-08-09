namespace EntraGuard.Shared.Detection;

/// <summary>
/// Social-engineering techniques EntraGuard recognises in a live authentication call.
///
/// These are drawn from observed identity-attack tradecraft rather than invented for
/// the demo: help-desk impersonation and MFA coaching are the documented entry vector
/// behind the large 2023-2024 identity intrusions. Each vector maps to something a
/// SOC analyst would recognise in an incident write-up.
/// </summary>
public enum ScamVector
{
    /// <summary>Caller coaches the victim to approve a push prompt they did not initiate.</summary>
    MfaFatigueCoaching,

    /// <summary>Caller asks the victim to read back a one-time code or number-matching digits.</summary>
    OtpElicitation,

    /// <summary>Caller claims to be IT, the help desk, Microsoft, or a security team.</summary>
    AuthorityImpersonation,

    /// <summary>Manufactured time pressure: account closure, breach in progress, "stay on the line".</summary>
    UrgencyPretexting,

    /// <summary>Caller directs the victim to install or launch remote-control software.</summary>
    RemoteAccessTooling,

    /// <summary>Caller solicits a Temporary Access Pass or other bootstrap credential.</summary>
    TemporaryAccessPassRequest,

    /// <summary>Caller attempts a credential or MFA-method reset by impersonating the user to the help desk.</summary>
    HelpdeskResetFraud,

    /// <summary>Caller redirects payment or banking details.</summary>
    PaymentRedirect,

    /// <summary>Caller tries to move the conversation to an attacker-controlled callback number or channel.</summary>
    CallbackNumberSwap,

    /// <summary>Caller walks the victim through registering a new, attacker-controlled MFA method.</summary>
    MfaMethodRegistration,
}

/// <summary>
/// How far the victim has been drawn into complying.
///
/// This is the signal that makes EntraGuard preventive rather than forensic: risk score
/// says "this is a scam", compliance stage says "how long until it succeeds". A high
/// score at <see cref="Unaware"/> warrants a warning; the same score at
/// <see cref="AboutToApprove"/> warrants interrupting the call.
///
/// Ordinal values are load-bearing — the policy gate compares them.
/// </summary>
public enum ComplianceStage
{
    /// <summary>Victim has not acted on the caller's instructions.</summary>
    Unaware = 0,

    /// <summary>Victim is engaged and following the caller's narrative.</summary>
    Engaged = 1,

    /// <summary>Victim is at the point of approving MFA, reading a code, or granting access.</summary>
    AboutToApprove = 2,

    /// <summary>Victim has already complied. Containment, not prevention.</summary>
    Approved = 3,
}
