'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import type { AccountInfo, PublicClientApplication } from '@azure/msal-browser';

async function establishSession(token: string) {
  const response = await fetch('/api/rp/session', { method: 'POST', headers: { Authorization: `Bearer ${token}` } });
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error ?? 'Could not establish a trusted session.'); }
}

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
  const scopeRef = useRef<string | null>(null);

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
    // Served at runtime rather than baked in, so the scope can change without a rebuild.
    scopeRef.current = (config.entraScope as string | undefined) ?? `api://${clientId}/VoiceProfile.Manage`;

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
            const sessionResponse = await fetch('/api/rp/session', { cache: 'no-store' });
            const session = sessionResponse.ok ? await sessionResponse.json() : null;
            if (!session || session.owner?.objectId?.toLowerCase() !== resolved.objectId.toLowerCase() || session.owner?.tenantId?.toLowerCase() !== resolved.tenantId.toLowerCase()) {
              try {
                const token = await msal.acquireTokenSilent({ scopes: [scopeRef.current!], account });
                await establishSession(token.accessToken);
              } catch { if (!cancelled) setState('signed-out'); return; }
            }
            if (cancelled) return;
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
        scopes: ['openid', 'profile', scopeRef.current!],
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

      await establishSession(result.accessToken);
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

  /**
   * An access token for EntraGuard's own API.
   *
   * Everything else in this app sends the user's object ID as a plain JSON field, which the
   * server has no way to verify. Voice enrolment cannot work that way: an object ID typed
   * by the browser would let anyone register their own voice against another account. So
   * these calls carry a token Entra signed, and the server reads the identity out of it.
   *
   * @param forceMfa
   *   Ask Entra for a session that satisfies multi-factor. Enrolment is a
   *   credential-registration event — doing it from a password-only session would turn a
   *   leaked password into a permanent biometric binding.
   */
  /**
   * Both tokens.
   *
   * The ID token is needed because amr — the record of HOW someone authenticated — cannot
   * be delivered in an access token at all. Entra emits it in ID tokens only, so proving a
   * second factor to the API requires sending the token that carries it. The server does
   * not trust it: it validates the signature and checks it describes the same person as the
   * access token.
   */
  const getTokens = useCallback(async (
    forceMfa = false,
  ): Promise<{ accessToken: string; idToken: string } | null> => {
    const msal = msalRef.current;
    const scope = scopeRef.current;
    if (!msal || !scope) return null;

    const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0];
    if (!account) return null;

    try {
      if (!forceMfa) {
        const silent = await msal.acquireTokenSilent({ scopes: [scope], account });
        return { accessToken: silent.accessToken, idToken: silent.idToken };
      }
    } catch {
      // Falls through to interactive, which is the expected path the first time this scope
      // is requested.
    }

    try {
      const result = await msal.acquireTokenPopup({
        scopes: [scope],
        account,

        // A fresh interactive sign-in, and NOTHING else.
        //
        // There is deliberately no claims challenge here. amr is not a requestable claim —
        // the claims parameter exists for acrs/acr, the Conditional Access authentication
        // context, and asking for amr made Entra refuse the sign-in outright with
        // AADSTS901001: "The 'amr' values request parameter value 'Empty' is invalid."
        //
        // amr arrives because it is configured as an optional claim on the ID TOKEN of the
        // app registration. prompt=login is what makes it describe what the user just did
        // rather than whatever established the session hours ago; if the tenant requires a
        // second factor, that is what the fresh sign-in performs and what amr then records.
        prompt: forceMfa ? 'login' : undefined,
      });
      return { accessToken: result.accessToken, idToken: result.idToken };
    } catch (tokenError) {
      setError(tokenError instanceof Error ? tokenError.message : String(tokenError));
      return null;
    }
  }, []);

  /** Just the access token, for calls that do not need to prove a second factor. */
  const getAccessToken = useCallback(
    async (forceMfa = false): Promise<string | null> =>
      (await getTokens(forceMfa))?.accessToken ?? null,
    [getTokens],
  );

  const signOut = useCallback(async () => {
    try {
      const response = await fetch('/api/rp/session', { method: 'DELETE' });
      if (!response.ok && response.status !== 401) throw new Error('Session revocation failed.');
    } catch {
      setError('Your server session could not be revoked. Please retry sign out.');
      return;
    }
    try {
      await msalRef.current?.logoutPopup();
    } catch {
      // Clearing local state is what matters here.
    }
    await msalRef.current?.clearCache();
    setIdentity(null);
    setState('signed-out');
  }, []);

  return { state, identity, error, signIn, signOut, getAccessToken, getTokens };
}
