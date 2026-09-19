'use client';
import { useEffect, useState } from 'react';
import { useEntraSignIn } from '@/components/rp/useEntraSignIn';
import '../app/rp.css';

export default function OperatorSignIn() {
  const auth = useEntraSignIn();
  const [message, setMessage] = useState('Use an authorized operator account to open the security console.');
  useEffect(() => {
    if (auth.state !== 'signed-in') return;
    let cancelled = false;
    void fetch('/api/operator/session', { cache: 'no-store' }).then(response => {
      if (cancelled) return;
      if (response.ok) window.location.assign('/');
      else setMessage('This account has no operator access, or the service is unavailable. Ask your administrator to check the operator role.');
    }).catch(() => { if (!cancelled) setMessage('The operator service is unavailable.'); });
    return () => { cancelled = true; };
  }, [auth.state]);
  return <main className="rp"><div className="rp-center"><div className="rp-card"><h1 className="rp-h1">EntraGuard operator sign-in</h1><p className="rp-sub" role="status">{message}</p>{auth.error && <p role="alert">{auth.error}</p>}<button className="rp-btn block" disabled={auth.state === 'loading' || auth.state === 'signing-in'} onClick={() => void auth.signIn()}>Sign in with Microsoft</button>{auth.state === 'signed-in' && <button className="rp-btn secondary block" onClick={() => void auth.signOut()}>Sign out</button>}</div></div></main>;
}
