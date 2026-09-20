'use client';
import { useEffect, useState } from 'react';
import { useEntraSignIn } from '@/components/rp/useEntraSignIn';
import '../(admin)/admin.css';
import { AdminGlyph } from '@/components/az/AdminGlyph';

export default function OperatorSignIn() {
  const auth = useEntraSignIn();
  const [message, setMessage] = useState('Use an authorized operator account to open the security console.');
  useEffect(() => {
    if (auth.state !== 'signed-in') return;
    let cancelled = false;
    void fetch('/api/operator/session', { cache: 'no-store' }).then(response => {
      if (cancelled) return;
      if (response.ok) { window.location.assign('/'); return; }
      // Three different problems with three different fixes, and they used to share one
      // sentence. A signed-in operator whose session had not been established yet was told
      // their account lacked operator access — which sent the reader to the directory to
      // fix something that was never wrong.
      if (response.status === 401) setMessage('Signed in, but the server session was not established. Sign out and sign in again.');
      else if (response.status === 403) setMessage(`${auth.identity?.upn ?? 'This account'} is not an operator of this deployment. Operator access is home-tenant only, and the account must hold the EntraGuard.Operator role or be listed in ENTRAGUARD_OPERATOR_IDS.`);
      else setMessage(`The operator service answered ${response.status}. This is a service problem, not an account problem.`);
    }).catch(() => { if (!cancelled) setMessage('The operator service could not be reached.'); });
    return () => { cancelled = true; };
  }, [auth.state, auth.identity]);
  return <main className="admin-portal admin-login"><header className="admin-login-header"><AdminGlyph name="shield" size={25}/>EntraGuard <span>Security console</span></header><section className="admin-login-card"><div className="admin-login-icon"><AdminGlyph name="shield" size={35}/></div><h1>Sign in to EntraGuard</h1><p className="admin-meta-note" role="status">{message}</p>{auth.error&&<p role="alert" className="az-msgbar error">{auth.error}</p>}<button className="az-cmd admin-primary" disabled={auth.state==='loading'||auth.state==='signing-in'} onClick={()=>void auth.signIn()}>{auth.state==='loading'?'Loading sign-in…':auth.state==='signing-in'?'Waiting for Microsoft…':'Sign in with Microsoft'}</button>{auth.state==='signed-in'&&<button className="az-cmd" onClick={()=>void auth.signOut()}>Sign out</button>}<p className="admin-meta-note">Operator access is verified by the service. Directory and security data remain scoped to the configured environment.</p><a href="https://docs.entraguard.my" target="_blank" rel="noreferrer">Read the operator handbook ↗</a></section></main>;
}
