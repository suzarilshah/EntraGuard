"""Diagrams for the EntraGuard write-up.

Hand-built SVG rather than a diagramming library: every box here is a real component with a
real name, and the arrows carry the protocol that actually runs over them. A generic
"cloud -> service" picture would have been quicker and would have said nothing.
"""

import pathlib

OUT = pathlib.Path(__file__).parent / "figures"
OUT.mkdir(exist_ok=True)

# Fluent-ish palette, printed dark-on-light because this ends up in a Word document.
INK = "#1b1a19"
MUTED = "#605e5c"
LINE = "#8a8886"
BLUE = "#0f6cbd"
BLUE_BG = "#eff6fc"
GREEN = "#0e700e"
GREEN_BG = "#effbef"
RED = "#a4262c"
RED_BG = "#fdf3f4"
AMBER = "#8a6100"
AMBER_BG = "#fff8e6"
GREY_BG = "#f3f2f1"

FONT = "Segoe UI, Helvetica, Arial, sans-serif"
MONO = "Consolas, SF Mono, monospace"


def esc(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def box(x, y, w, h, title, lines=(), fill=GREY_BG, stroke=LINE, title_colour=INK, r=6):
    out = [
        f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{r}" '
        f'fill="{fill}" stroke="{stroke}" stroke-width="1.5"/>',
        f'<text x="{x + 14}" y="{y + 24}" font-family="{FONT}" font-size="14.5" '
        f'font-weight="600" fill="{title_colour}">{esc(title)}</text>',
    ]
    for i, line in enumerate(lines):
        out.append(
            f'<text x="{x + 14}" y="{y + 45 + i * 17}" font-family="{FONT}" font-size="12" '
            f'fill="{MUTED}">{esc(line)}</text>'
        )
    return "\n".join(out)


def arrow(x1, y1, x2, y2, label="", colour=LINE, dash="", label_dy=-7, anchor="middle"):
    mid_x, mid_y = (x1 + x2) / 2, (y1 + y2) / 2
    d = f' stroke-dasharray="{dash}"' if dash else ""
    out = [
        f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{colour}" '
        f'stroke-width="1.8" marker-end="url(#arrow)"{d}/>'
    ]
    if label:
        out.append(
            f'<text x="{mid_x}" y="{mid_y + label_dy}" font-family="{FONT}" font-size="11.5" '
            f'fill="{colour}" text-anchor="{anchor}">{esc(label)}</text>'
        )
    return "\n".join(out)


def label(x, y, text, size=12, colour=MUTED, weight="400", anchor="start", font=FONT):
    return (
        f'<text x="{x}" y="{y}" font-family="{font}" font-size="{size}" fill="{colour}" '
        f'font-weight="{weight}" text-anchor="{anchor}">{esc(text)}</text>'
    )


def svg(width, height, body, title):
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}"
     viewBox="0 0 {width} {height}" font-family="{FONT}">
  <defs>
    <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7"
            orient="auto-start-reverse">
      <path d="M 0 0 L 10 5 L 0 10 z" fill="{LINE}"/>
    </marker>
  </defs>
  <rect width="{width}" height="{height}" fill="#ffffff"/>
  <title>{esc(title)}</title>
{body}
</svg>"""


# ─────────────────────────────────────────────────────────────────────────────
# Figure 1 — the whole system
# ─────────────────────────────────────────────────────────────────────────────
def figure_architecture():
    b = []
    b.append(label(40, 34, "Figure 1 \u2014 What talks to what, and over which protocol", 16, INK, "600"))
    b.append(label(40, 56, "Every arrow is a real protocol on a real wire. Nothing here is illustrative.", 12, MUTED))

    b.append(box(40, 92, 236, 92, "Contoso Treasury", [
        "Next.js \u00b7 the relying party", "MSAL sign-in, then asks", "EntraGuard for a step-up",
    ], BLUE_BG, BLUE, BLUE))
    b.append(box(40, 212, 236, 92, "EntraGuard console", [
        "Next.js \u00b7 server-side KQL", "Four blades, all live data", "No secrets in the browser",
    ], BLUE_BG, BLUE, BLUE))

    b.append(box(452, 128, 268, 150, "Media service (.NET 9)", [
        "Verification state machine",
        "Coercion analyst \u00b7 policy gate",
        "Voice capture and scoring",
        "Risk scoring \u00b7 fault recorder",
        "Container Apps, one replica",
    ], GREEN_BG, GREEN, GREEN))

    b.append(box(952, 40, 260, 76, "Azure Communication", [
        "Call Automation places the call", "Unmixed PCM 24 kHz, duplex",
    ]))
    b.append(box(952, 140, 260, 76, "Microsoft Teams", [
        "Cross-tenant federation", "The call rings in Teams",
    ]))
    b.append(box(952, 240, 260, 76, "Azure OpenAI", [
        "Analyst scores the transcript", "every 3s over a 45s window",
    ]))
    b.append(box(952, 340, 260, 76, "Voiceprint sidecar", [
        "FastAPI + SpeechBrain ECAPA", "Internal ingress, kept warm",
    ]))

    b.append(box(452, 320, 268, 96, "Microsoft Entra ID", [
        "Graph: auditLogs/signIns",
        "supplies the live questions",
        "Workload identity federation",
    ], AMBER_BG, AMBER, AMBER))

    b.append(box(452, 456, 760, 84, "Microsoft Sentinel \u00b7 Log Analytics", [
        "Verification_CL \u00b7 Biometric_CL \u00b7 Fault_CL \u00b7 CallAnalysis_CL \u00b7 Remediation_CL",
        "Logs Ingestion API through a DCE and a DCR \u2014 remember the DCR, it bites later",
    ]))

    b.append(arrow(276, 150, 452, 172, "", BLUE))
    b.append(label(364, 148, "POST /api/verify/start", 11.5, BLUE, "600", "middle"))
    b.append(arrow(452, 250, 276, 262, "", BLUE))
    b.append(label(364, 246, "KQL, server-side", 11.5, BLUE, "600", "middle"))

    b.append(arrow(720, 150, 952, 78, "", LINE))
    b.append(label(836, 100, "CreateCall", 11.5, MUTED, "600", "middle"))

    b.append(arrow(720, 185, 952, 178, "", LINE))
    b.append(label(836, 172, "WebSocket \u00b7 unmixed audio", 11.5, MUTED, "600", "middle"))

    b.append(arrow(720, 220, 952, 278, "", LINE))
    b.append(label(836, 246, "45s transcript window", 11.5, MUTED, "600", "middle"))

    b.append(arrow(720, 255, 952, 378, "", LINE))
    b.append(label(836, 330, "PCM 16 kHz \u2192 192-d embedding", 11.5, MUTED, "600", "middle"))

    b.append(arrow(1082, 116, 1082, 140, "", LINE))
    b.append(label(1092, 134, "interop", 11, MUTED))

    b.append(arrow(586, 278, 586, 320, "", AMBER))
    b.append(arrow(586, 416, 586, 456, "", LINE))

    b.append(label(40, 352, "The phone never talks", 12.5, INK, "600"))
    b.append(label(40, 372, "to EntraGuard directly.", 12, MUTED))
    b.append(label(40, 390, "It answers a Teams call,", 12, MUTED))
    b.append(label(40, 408, "and that is the point: the", 12, MUTED))
    b.append(label(40, 426, "channel is a managed", 12, MUTED))
    b.append(label(40, 444, "identity, not a phone number.", 12, MUTED))

    return svg(1252, 570, "\n".join(b), "EntraGuard architecture")


# ─────────────────────────────────────────────────────────────────────────────
# Figure 2 — what happens on the call, second by second
# ─────────────────────────────────────────────────────────────────────────────
def figure_call_flow():
    b = []
    b.append(label(40, 34, "Figure 2 — One verification call, second by second", 16, INK, "600"))
    b.append(label(40, 56, "Time runs downward. The right-hand column is what the system is doing while the caller talks.", 12, MUTED))

    steps = [
        ("Browser", "User signs in to Contoso Treasury and asks to release a payment run.", BLUE_BG, BLUE),
        ("Screen", "A two-digit number appears in the browser. It is never sent to the phone.", BLUE_BG, BLUE),
        ("Teams", "The phone rings. EntraGuard speaks: enter the number on your keypad.", GREY_BG, LINE),
        ("Keypad", "DTMF arrives on two independent paths. Both are de-duplicated to one entry.", GREY_BG, LINE),
        ("Spoken", "“Before we continue — make sure nobody can overhear you.”", AMBER_BG, AMBER),
        ("Spoken", "Which town or city were you in the last time you signed in?", AMBER_BG, AMBER),
        ("Spoken", "What kind of device or browser did you last sign in from?", AMBER_BG, AMBER),
        ("Listening", "Voice capture is OPEN only here — see Figure 3 for why that matters.", GREEN_BG, GREEN),
        ("Verdict", "Adjudicate: code, coercion, then voice. Speak the result. Hang up.", GREEN_BG, GREEN),
    ]

    y = 86
    for i, (who, what, fill, stroke) in enumerate(steps):
        b.append(f'<rect x="40" y="{y}" width="110" height="46" rx="5" fill="{fill}" stroke="{stroke}" stroke-width="1.4"/>')
        b.append(label(52, y + 28, who, 12.5, stroke, "600"))
        b.append(f'<rect x="168" y="{y}" width="742" height="46" rx="5" fill="#ffffff" stroke="{LINE}" stroke-width="1"/>')
        b.append(label(182, y + 28, what, 12.5, INK))
        if i < len(steps) - 1:
            b.append(f'<line x1="95" y1="{y + 46}" x2="95" y2="{y + 60}" stroke="{LINE}" stroke-width="1.6" marker-end="url(#arrow)"/>')
        y += 60

    b.append(label(40, y + 18, "Throughout: the coercion analyst re-scores the live transcript every three seconds.", 12.5, RED, "600"))
    b.append(label(40, y + 38, "A correct code entered under instruction is refused. That is the whole point of the product.", 12.5, MUTED))

    return svg(950, y + 60, "\n".join(b), "Verification call flow")


# ─────────────────────────────────────────────────────────────────────────────
# Figure 3 — the bug I am most embarrassed about
# ─────────────────────────────────────────────────────────────────────────────
def figure_voice_bug():
    b = []
    b.append(label(40, 34, "Figure 3 — Why a genuine speaker scored 0.07 against their own voiceprint", 16, INK, "600"))
    b.append(label(40, 56, "The buffer was never cleared on a verification call, so it filled with our own synthesised prompts.", 12, MUTED))

    # Before
    b.append(label(40, 96, "BEFORE — what was actually compared", 13, RED, "600"))
    segs = [
        (40, 300, "EntraGuard speaking (TTS)", RED_BG, RED),
        (340, 90, "caller", GREEN_BG, GREEN),
        (430, 240, "EntraGuard speaking (TTS)", RED_BG, RED),
        (670, 70, "caller", GREEN_BG, GREEN),
        (740, 170, "EntraGuard speaking (TTS)", RED_BG, RED),
    ]
    for x, w, text, fill, stroke in segs:
        b.append(f'<rect x="{x}" y="112" width="{w}" height="40" fill="{fill}" stroke="{stroke}" stroke-width="1.2"/>')
        if w > 100:
            b.append(label(x + w / 2, 137, text, 11, stroke, "600", "middle"))
    b.append(label(920, 137, "→ 0.07", 15, RED, "700"))
    b.append(label(40, 172, "Roughly 80% of the buffer was a synthetic voice. ECAPA compared the template against that,", 12, MUTED))
    b.append(label(40, 190, "and correctly reported a different speaker — because it was one. The model was never wrong.", 12, MUTED))

    # After
    b.append(label(40, 234, "AFTER — capture bracketed by the same wait the transcript already used", 13, GREEN, "600"))
    b.append(f'<rect x="40" y="250" width="880" height="40" fill="#ffffff" stroke="{LINE}" stroke-width="1.2" stroke-dasharray="4 3"/>')
    b.append(label(190, 275, "capture CLOSED", 11.5, MUTED, "600", "middle"))
    b.append(label(520, 275, "capture CLOSED", 11.5, MUTED, "600", "middle"))
    b.append(label(830, 275, "capture CLOSED", 11.5, MUTED, "600", "middle"))
    for x, w in ((340, 90), (670, 70)):
        b.append(f'<rect x="{x}" y="250" width="{w}" height="40" fill="{GREEN_BG}" stroke="{GREEN}" stroke-width="1.4"/>')
    b.append(label(385, 275, "caller", 11, GREEN, "600", "middle"))
    b.append(label(705, 275, "caller", 11, GREEN, "600", "middle"))
    b.append(label(920, 275, "→ genuine band", 13, GREEN, "700"))

    b.append(label(40, 322, "Three more faults hid in the same place:", 12.5, INK, "600"))
    b.append(label(40, 344, "•  The 60-second cap dropped NEW frames once full — it kept the prompts and discarded the answers.", 12, MUTED))
    b.append(label(40, 364, "•  Scoring ran from one call site only, so a call that passed on the code alone compared nothing at all.", 12, MUTED))
    b.append(label(40, 384, "•  And 'NotAssessed' reached Sentinel as a score of 0 — identical to a genuine zero.", 12, MUTED))

    return svg(1040, 410, "\n".join(b), "Voice capture defect")


# ─────────────────────────────────────────────────────────────────────────────
# Figure 4 — factor strength, which is the opposite of what people expect
# ─────────────────────────────────────────────────────────────────────────────
def figure_factors():
    b = []
    b.append(label(40, 34, "Figure 4 — Which factor actually resists a cloned voice", 16, INK, "600"))
    b.append(label(40, 56, "The ranking is the reverse of the one most demos imply. The bars are my engineering judgement, not measured figures \u2014 the", 12, MUTED))
    b.append(label(40, 74, "only number here that came from an experiment is the voice one, and it is the weakest.", 12, MUTED))

    rows = [
        ("Number match", "A two-digit code shown in the browser, keyed on the phone.",
         "A cloned voice cannot see the screen.", 96, GREEN, GREEN_BG),
        ("Live telemetry", "Where you signed in from an hour ago, on what device.",
         "Unpredictable, expires by itself, nothing stored to steal.", 88, GREEN, GREEN_BG),
        ("Coercion analysis", "An LLM listening for someone being talked through the call.",
         "The only factor that asks whether the user means it.", 74, BLUE, BLUE_BG),
        ("Voice biometrics", "ECAPA-TDNN against a template enrolled once.",
         "Degrades over telephony. No anti-spoofing whatsoever.", 34, AMBER, AMBER_BG),
        ("Stored questions", "Your first pet.",
         "NIST 800-63 rejects these outright. Kept only as a fallback.", 14, RED, RED_BG),
    ]

    y = 104
    for name, what, why, strength, colour, fill in rows:
        b.append(f'<rect x="40" y="{y}" width="180" height="62" rx="5" fill="{fill}" stroke="{colour}" stroke-width="1.5"/>')
        b.append(label(54, y + 26, name, 13, colour, "600"))
        b.append(label(54, y + 46, f"judged {strength}/100", 11, MUTED))
        b.append(label(240, y + 24, what, 12.5, INK))
        b.append(label(240, y + 44, why, 12, MUTED))
        b.append(f'<rect x="700" y="{y + 22}" width="240" height="16" rx="8" fill="{GREY_BG}"/>')
        b.append(f'<rect x="700" y="{y + 22}" width="{strength * 2.4:.0f}" height="16" rx="8" fill="{colour}"/>')
        y += 74

    b.append(label(40, y + 22, "Voice is the part a demo notices and the part an attacker defeats most easily.", 13, INK, "600"))
    b.append(label(40, y + 42, "We shipped it anyway — but as a signal that asks for a stronger factor, never as a gate that can deny you alone.", 12, MUTED))

    return svg(980, y + 70, "\n".join(b), "Factor strength")


# ─────────────────────────────────────────────────────────────────────────────
# Figure 5 — silent degradation, the thing that cost the most time
# ─────────────────────────────────────────────────────────────────────────────
def figure_supportability():
    b = []
    b.append(label(40, 34, "Figure 5 — Every expensive bug had the same shape", 16, INK, "600"))
    b.append(label(40, 56, "It worked. With a weaker mechanism. And nothing said so.", 12, MUTED))

    cases = [
        ("Telemetry fell back", "Graph refused the sign-in logs",
         "Caller heard only ‘your first pet’", "Looked like a deleted feature"),
        ("Columns dropped", "The DCR did not declare them",
         "Dashboard read zero", "Identical to ‘nothing happened’"),
        ("Voice never scored", "Buffer held our own speech",
         "0.07 for the genuine user", "Looked like a bad model"),
        ("Risk always zero", "Captured before anyone spoke",
         "Every call scored 0 risk", "Looked like a calm week"),
    ]

    x = 40
    for what, why, saw, mistaken in cases:
        b.append(f'<rect x="{x}" y="86" width="228" height="176" rx="6" fill="{RED_BG}" stroke="{RED}" stroke-width="1.4"/>')
        b.append(label(x + 14, 112, what, 13.5, RED, "600"))
        b.append(label(x + 14, 140, "Cause", 10.5, MUTED, "600"))
        b.append(label(x + 14, 158, why, 11.5, INK))
        b.append(label(x + 14, 186, "What we saw", 10.5, MUTED, "600"))
        b.append(label(x + 14, 204, saw, 11.5, INK))
        b.append(label(x + 14, 232, "Mistaken for", 10.5, MUTED, "600"))
        b.append(label(x + 14, 250, mistaken, 11.5, INK))
        x += 244

    b.append(f'<rect x="40" y="292" width="944" height="96" rx="6" fill="{GREEN_BG}" stroke="{GREEN}" stroke-width="1.5"/>')
    b.append(label(58, 320, "What we added, once the pattern was obvious", 14, GREEN, "600"))
    b.append(label(58, 344, "A fault is not a log line. It records four things, none optional: what failed, what the USER experienced,", 12.5, INK))
    b.append(label(58, 364, "the probable cause, and the next action. If you cannot fill in the last three, it is not worth recording.", 12.5, INK))

    return svg(1024, 410, "\n".join(b), "Silent degradation")


FIGURES = {
    "fig1-architecture": figure_architecture,
    "fig2-call-flow": figure_call_flow,
    "fig3-voice-bug": figure_voice_bug,
    "fig4-factors": figure_factors,
    "fig5-supportability": figure_supportability,
}

for name, fn in FIGURES.items():
    path = OUT / f"{name}.svg"
    path.write_text(fn(), encoding="utf-8")
    print(f"wrote {path.name}")
