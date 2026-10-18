'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import type { Auth, User } from 'firebase/auth';

/**
 * The Google Cloud half of EntraGuard (github.com/suzarilshah/entraguard-gemini).
 *
 * Public identifiers only: a Firebase web config is not a secret, and the analyst API
 * refuses every request without a valid Firebase ID token. Treasury's own origin is on the
 * project's authorized-domain list, which is what lets the sign-in popup return here.
 */
export const GOOGLE_ANALYST_URL = 'https://entraguard-analyst-903885657225.asia-southeast1.run.app';
export const GOOGLE_CONSOLE_URL = 'https://entraguard-gemini.web.app';

const FIREBASE_CONFIG = {
  apiKey: 'AIzaSyCj8hiInQOH6vD8meseJAaGksNbPSnE-R4',
  authDomain: 'entraguard-gemini.firebaseapp.com',
  projectId: 'entraguard-gemini',
  appId: '1:903885657225:web:4f75302c686f25556c6258',
};

export interface GoogleIdentity {
  uid: string;
  email: string;
  displayName: string;
}

export type GoogleSignInState = 'loading' | 'signed-out' | 'signing-in' | 'signed-in';

/**
 * Google sign-in for Contoso Treasury, through Firebase Authentication.
 *
 * A second way in alongside Microsoft, not a replacement. A Google account has no Entra
 * directory behind it, so its step-up runs on the Gemini analyst instead of the Teams/ACS
 * call — see GoogleVerification. Firebase is imported lazily for the same reason MSAL is:
 * it touches browser globals and would break the server render.
 */
export function useGoogleSignIn() {
  const [state, setState] = useState<GoogleSignInState>('loading');
  const [identity, setIdentity] = useState<GoogleIdentity | null>(null);
  const [error, setError] = useState<string | null>(null);
  const authRef = useRef<Auth | null>(null);

  useEffect(() => {
    let unsubscribe: (() => void) | undefined;
    let cancelled = false;
    void (async () => {
      const [{ initializeApp, getApps }, { getAuth, onAuthStateChanged, getRedirectResult }] = await Promise.all([
        import('firebase/app'),
        import('firebase/auth'),
      ]);
      if (cancelled) return;
      const app = getApps().find((a) => a.name === 'entraguard-gemini') ?? initializeApp(FIREBASE_CONFIG, 'entraguard-gemini');
      const auth = getAuth(app);
      authRef.current = auth;
      getRedirectResult(auth).catch((e: { message?: string }) => setError(e.message ?? 'Google sign-in failed.'));
      unsubscribe = onAuthStateChanged(auth, (user: User | null) => {
        setIdentity(user ? { uid: user.uid, email: user.email ?? '', displayName: user.displayName ?? user.email ?? 'Google user' } : null);
        setState(user ? 'signed-in' : 'signed-out');
      });
    })();
    return () => { cancelled = true; unsubscribe?.(); };
  }, []);

  const signIn = useCallback(async () => {
    const auth = authRef.current;
    if (!auth) return;
    setError(null);
    setState('signing-in');
    const { GoogleAuthProvider, signInWithPopup, signInWithRedirect } = await import('firebase/auth');
    const provider = new GoogleAuthProvider();
    try {
      await signInWithPopup(auth, provider);
    } catch (e) {
      const code = (e as { code?: string }).code;
      if (code === 'auth/popup-closed-by-user' || code === 'auth/cancelled-popup-request') { setState('signed-out'); return; }
      if (code === 'auth/popup-blocked' || code === 'auth/operation-not-supported-in-this-environment') {
        await signInWithRedirect(auth, provider);
        return;
      }
      setError((e as { message?: string }).message ?? 'Google sign-in failed.');
      setState('signed-out');
    }
  }, []);

  const signOut = useCallback(async () => {
    const auth = authRef.current;
    if (!auth) return;
    const { signOut: firebaseSignOut } = await import('firebase/auth');
    await firebaseSignOut(auth);
  }, []);

  /** Fresh Firebase ID token for the analyst API. */
  const getIdToken = useCallback(async () => {
    const user = authRef.current?.currentUser;
    if (!user) throw new Error('Your Google sign-in has expired. Sign in again.');
    return user.getIdToken();
  }, []);

  return { state, identity, error, signIn, signOut, getIdToken };
}

/** Call the analyst API with the signed-in user's Firebase ID token. */
export async function analystFetch<T>(getIdToken: () => Promise<string>, path: string, body?: unknown): Promise<T> {
  const token = await getIdToken();
  const response = await fetch(`${GOOGLE_ANALYST_URL}${path}`, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw Object.assign(new Error(data.error ?? `Request failed (${response.status})`), { status: response.status });
  return data as T;
}
