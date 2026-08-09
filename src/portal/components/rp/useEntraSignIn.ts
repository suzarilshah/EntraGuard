'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import type { AccountInfo, PublicClientApplication } from '@azure/msal-browser';

/**
 * The signed-in work identity, as asserted by Entra ID.
 *
 * Every field here comes from a token Entra issued, not from anything the browser typed.
 * That distinction is the entire point: the previous build asked the user to paste the
 * object ID of the account to call, which means the "second factor" could be aimed at
 * somebody else's phone. A factor you can redirect is not a factor.
 */
export interface WorkIdentity {
  /** Entra object ID — also the identifier a Teams call is placed to. */
  objectId: string;
  /** Home tenant. Teams calling only works if this tenant federated with our ACS resource. */
  tenantId: string;
  upn: string;
  displayName: string;
}

export type SignInState = 'loading' | 'signed-out' | 'signing-in' | 'signed-in' | 'error';

/**
 * Entra ID sign-in for the relying-party demo app.
 *
 * Multitenant (`/organizations`): any work or school account can sign in, which is what
 * makes this demonstrable on a judge's own tenant rather than only ours. Personal Microsoft
 * accounts are deliberately excluded — they have no Teams work identity, so they would sign
 * in successfully and then dead-end at the second factor.
 *
 * MSAL is imported lazily for the same reason the calling SDK is: it touches browser
 * globals at module scope and would break the server render.
 */
export function useEntraSignIn() {
  const [state, setState] = useState<SignInState>('loading');
  const [identity, setIdentity] = useState<WorkIdentity | null>(null);
  const [error, setError] = useState<string | null>(null);

  const msalRef = useRef<PublicClientApplication | null>(null);
  const clientIdRef = useRef<string | null>(null);

  const toIdentity = (account: AccountInfo): WorkIdentity | null => {
    const claims = (account.idTokenClaims ?? {}) as Record<string, unknown>;
    const objectId = typeof claims.oid === 'string' ? claims.oid : null;
    const tenantId = typeof claims.tid === 'string' ? claims.tid : account.tenantId;

    // No oid means no callable Teams identity. Reporting a partial identity here would
    // send the flow onward to a call that can never be placed.
    if (!objectId || !tenantId) return null;

    return {
      objectId,
      tenantId,
      upn: account.username,
      displayName: account.name ?? account.username,
    };
  };

  /** Create MSAL against the client ID the server holds, and resume any existing session. */
  const ensureClient = useCallback(async () => {
    if (msalRef.current) return msalRef.current;

    const response = await fetch('/api/rp/config', { cache: 'no-store' });
    const config = response.ok ? await response.json() : {};
    const clientId = config.entraClientId as string | undefined;

    if (!clientId) {
      throw new Error(
        'Entra sign-in is not configured on this deployment (ENTRA_RP_CLIENT_ID is unset).',
      );
    }
    clientIdRef.current = clientId;

    const { PublicClientApplication } = await import('@azure/msal-browser');
    const msal = new PublicClientApplication({
      auth: {
        clientId,
        // 'organizations' = any Entra tenant, work accounts only.
        authority: 'https://login.microsoftonline.com/organizations',
        redirectUri: `${window.location.origin}/app`,
      },
      cache: { cacheLocation: 'sessionStorage' },
    });

    await msal.initialize();
    msalRef.current = msal;
    return msal;
  }, []);

  useEffect(() => {
    let cancelled = false;

    (async () => {
      try {
        const msal = await ensureClient();

        // Completes a redirect sign-in if we came back from one.
        const redirect = await msal.handleRedirectPromise();
        const account = redirect?.account ?? msal.getAllAccounts()[0] ?? null;

        if (cancelled) return;

        if (account) {
          msal.setActiveAccount(account);
          const resolved = toIdentity(account);
          if (resolved) {
            setIdentity(resolved);
            setState('signed-in');
            return;
          }
        }
        setState('signed-out');
      } catch (initError) {
        if (cancelled) return;
        setError(initError instanceof Error ? initError.message : String(initError));
        setState('error');
      }
    })();

    return () => { cancelled = true; };
  }, [ensureClient]);

  const signIn = useCallback(async () => {
    setError(null);
    setState('signing-in');

    try {
      const msal = await ensureClient();
      const result = await msal.loginPopup({
        scopes: ['openid', 'profile', 'User.Read'],
        prompt: 'select_account',
      });

      msal.setActiveAccount(result.account);
      const resolved = toIdentity(result.account);

      if (!resolved) {
        throw new Error(
          'That account has no directory object ID, so it cannot be reached on Teams. ' +
          'Sign in with a work or school account.',
        );
      }

      setIdentity(resolved);
      setState('signed-in');
    } catch (signInError) {
      const message = signInError instanceof Error ? signInError.message : String(signInError);
      // A closed popup is a decision, not a fault.
      if (message.includes('user_cancelled') || message.includes('popup_window_error')) {
        setState('signed-out');
        return;
      }
      setError(message);
      setState('error');
    }
  }, [ensureClient]);

  const signOut = useCallback(async () => {
    try {
      await msalRef.current?.logoutPopup();
    } catch {
      // Clearing local state is what matters here.
    }
    setIdentity(null);
    setState('signed-out');
  }, []);

  return { state, identity, error, signIn, signOut };
}
