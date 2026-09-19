import type { CSSProperties } from 'react';

/** Presentation only. Authentication and call state stay in TreasuryApp. */
export function TreasuryIcon({ kind, size = 20 }: {
  kind: 'shield' | 'arrow' | 'lock' | 'voice' | 'user' | 'check';
  size?: number;
}) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {kind === 'shield' && <><path d="m12 3 8 3v5c0 5-3.5 8-8 10-4.5-2-8-5-8-10V6l8-3Z" /><path d="m8.5 12 2.5 2.5 4.5-5" /></>}
      {kind === 'arrow' && <><path d="M5 12h14M13 6l6 6-6 6" /></>}
      {kind === 'lock' && <><rect x="5" y="10" width="14" height="11" rx="3" /><path d="M8 10V7a4 4 0 0 1 8 0v3M12 14v3" /></>}
      {kind === 'voice' && <><path d="M4 10v4M8 6v12M12 3v18M16 7v10M20 10v4" /></>}
      {kind === 'user' && <><circle cx="12" cy="8" r="4" /><path d="M4 21v-2a8 8 0 0 1 16 0v2" /></>}
      {kind === 'check' && <path d="m5 12 4 4L19 6" />}
    </svg>
  );
}

export function TreasuryBrand() {
  return (
    <span className="treasury-brand">
      <span className="treasury-brand-mark" aria-hidden="true">
        <svg width="29" height="29" viewBox="0 0 32 32" fill="none">
          <path d="M23 5H12L5 12v15h8V15l10-10Z" fill="currentColor" />
          <path d="M27 9 17 19v8h10V9Z" fill="currentColor" opacity=".55" />
        </svg>
      </span>
      <span><span className="treasury-brand-name">contoso<span className="treasury-brand-dot">.</span></span>
        <span className="treasury-brand-descriptor">TREASURY</span></span>
    </span>
  );
}

function TreasuryNetwork() {
  return (
    <div className="treasury-network" aria-hidden="true">
      <svg className="treasury-globe" viewBox="0 0 640 380" fill="none">
        <defs>
          <radialGradient id="treasury-glow"><stop stopColor="#c2e49c" stopOpacity=".13" /><stop offset="1" stopColor="#c2e49c" stopOpacity="0" /></radialGradient>
          <linearGradient id="treasury-route" x1="85" y1="250" x2="510" y2="90" gradientUnits="userSpaceOnUse"><stop stopColor="#cee8a0" stopOpacity=".1" /><stop offset=".55" stopColor="#d9f3ad" /><stop offset="1" stopColor="#cee8a0" stopOpacity=".35" /></linearGradient>
        </defs>
        <ellipse cx="320" cy="207" rx="305" ry="185" fill="url(#treasury-glow)" />
        <g stroke="#c8dec7" strokeOpacity=".14">
          <ellipse cx="320" cy="207" rx="237" ry="148" />
          <ellipse cx="320" cy="207" rx="178" ry="148" />
          <ellipse cx="320" cy="207" rx="95" ry="148" />
          <ellipse cx="320" cy="207" rx="237" ry="100" />
          <ellipse cx="320" cy="207" rx="237" ry="48" />
          <path d="M83 207h474M320 59v296M109 140h422M109 274h422" />
        </g>
        <path d="M131 249C213 49 343 49 504 149" stroke="url(#treasury-route)" strokeWidth="2" />
        <path d="M208 123c92 25 141 142 240 155" stroke="#d9f3ad" strokeOpacity=".5" strokeDasharray="4 6" />
        <path d="M131 249c104 37 266 14 373-100" stroke="#d9f3ad" strokeOpacity=".25" />
        {[{ x: 131, y: 249 }, { x: 208, y: 123 }, { x: 504, y: 149 }, { x: 448, y: 278 }].map(({ x, y }) => (
          <g key={x}><circle cx={x} cy={y} r="14" fill="#d9f3ad" fillOpacity=".07" /><circle cx={x} cy={y} r="5" fill="#d9f3ad" /><circle cx={x} cy={y} r="9" stroke="#d9f3ad" strokeOpacity=".35" /></g>
        ))}
        <g fill="#a9bbb6" fontSize="10" letterSpacing="2" fontFamily="monospace">
          <text x="88" y="283">NEW YORK</text><text x="173" y="98">LONDON</text><text x="465" y="127">SINGAPORE</text>
        </g>
      </svg>
      <div className="treasury-signal-card">
        <span className="treasury-signal-icon"><TreasuryIcon kind="voice" size={23} /></span>
        <span className="treasury-signal-copy"><strong>A voice. An identity.</strong><span>Confidence beyond credentials.</span></span>
        <div className="treasury-wave">
          {[8, 14, 24, 13, 30, 20, 10, 26, 17, 8].map((height, index) => (
            <i key={index} style={{ '--bar-height': `${height}px`, '--bar-delay': `${index * -0.17}s` } as CSSProperties} />
          ))}
        </div>
      </div>
    </div>
  );
}

export function TreasuryStory() {
  return (
    <aside className="treasury-story" aria-label="Contoso Treasury, protected by EntraGuard">
      <div className="treasury-story-content">
        <div className="treasury-eyebrow"><span />THE CONFIDENCE TO MOVE FORWARD</div>
        <h2>Your capital.<br />Your control.<br /><em>Your peace of mind.</em></h2>
        <p className="treasury-story-description">Extraordinary ambition deserves exceptional protection. A more considered way to access your financial world.</p>
      </div>
      <TreasuryNetwork />
      <div className="treasury-story-bottom">
        <div className="treasury-story-rule" />
        <div className="treasury-story-note"><TreasuryIcon kind="shield" size={22} /><p><strong>Protection that goes beyond a password.</strong><span>Identity verification. A human conversation. An added layer of trust.</span></p></div>
        <div className="treasury-story-caption"><span>CONTOSO TREASURY</span><span>POWERED BY ENTRAGUARD</span></div>
      </div>
    </aside>
  );
}

export function TreasuryJourney({ stage }: { stage: string }) {
  const current = stage === 'login' ? 0 : stage === 'granted' ? 2 : 1;
  return (
    <ol className="treasury-journey" aria-label="Secure access progress">
      {['Sign in', 'Verify identity', 'Access treasury'].map((label, index) => (
        <li key={label} className={index < current ? 'complete' : index === current ? 'current' : ''}
          aria-current={index === current ? 'step' : undefined}>
          <span className="treasury-journey-number">{index < current ? <TreasuryIcon kind="check" size={12} /> : `0${index + 1}`}</span>
          <span>{label}</span>
        </li>
      ))}
    </ol>
  );
}

export function TreasuryProtection() {
  return (
    <section className="treasury-protection" aria-labelledby="treasury-protection-title" id="treasury-protection">
      <div className="treasury-section-label" id="treasury-protection-title">A MORE INTELLIGENT LAYER OF PROTECTION</div>
      <div className="treasury-protection-row"><span className="treasury-feature-icon"><TreasuryIcon kind="user" /></span><div><h3>Your work identity</h3><p>Sign in securely with Microsoft Entra ID.</p></div></div>
      <div className="treasury-protection-row"><span className="treasury-feature-icon"><TreasuryIcon kind="voice" /></span><div><h3>Your voice. Your verification.</h3><p>A verification call connects you to your sign-in.</p></div></div>
      <div className="treasury-protection-row"><span className="treasury-feature-icon"><TreasuryIcon kind="shield" /></span><div><h3>Protection in the moment</h3><p>EntraGuard checks for signs of coaching during the call.</p></div></div>
      <details className="treasury-disclosure">
        <summary>How your verification works <span aria-hidden="true">+</span></summary>
        <div className="treasury-disclosure-body">After Microsoft sign-in, choose Teams, this browser, or your phone. Enter the number shown on screen using the call keypad, then answer any identity questions. Voice-profile enrollment is optional. The verification result determines whether Treasury access is released.</div>
      </details>
    </section>
  );
}

export function TreasuryHelp() {
  return (
    <details className="treasury-help" id="treasury-sign-in-help">
      <summary>Need help signing in? <TreasuryIcon kind="arrow" size={15} /></summary>
      <div className="treasury-help-body">
        <strong>Use your Microsoft work or school account.</strong>
        <p>Allow the Microsoft sign-in pop-up if your browser blocks it. Personal Microsoft accounts are not supported.</p>
        <p>If your account cannot sign in, contact your organization’s IT help desk using a number you already know. You can choose your verification device after signing in.</p>
      </div>
    </details>
  );
}
