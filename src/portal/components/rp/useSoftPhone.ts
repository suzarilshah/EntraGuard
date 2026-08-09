'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import type { AzureCommunicationTokenCredential } from '@azure/communication-common';
import type { Call, CallAgent, IncomingCall } from '@azure/communication-calling';

// Types only at module scope. The ACS Calling SDK touches browser globals (MediaStream,
// navigator.mediaDevices) the moment it is evaluated, so a static import crashes Next's
// prerender with "MediaStream is not defined". Loading it inside register() also keeps
// roughly a megabyte of SDK off the initial page, since most visitors to a login screen
// never reach the voice-verification step.

export type PhoneState =
  | 'idle'
  | 'registering'
  | 'ready'
  | 'ringing'
  | 'connected'
  | 'ended'
  | 'error';

/** What the browser can actually do, established BEFORE a call is placed. */
export interface DeviceCheck {
  /** Microphone usable. False means coercion monitoring will be blind. */
  microphone: boolean;
  /** Why the microphone is unusable, in the browser's own words. */
  reason?: string;
  checked: boolean;
}

/**
 * A browser soft-phone that receives EntraGuard's verification call.
 *
 * Stands in for the user's handset. No PSTN number is provisioned on this subscription —
 * number purchase returns 403 InsufficientPermissions — so the call is placed to an ACS
 * identity rather than a phone number. Opening the companion /phone page on a mobile
 * registers that same identity there, so the call genuinely rings on the user's phone;
 * it travels over data instead of the phone network.
 *
 * Everything else is real: real ACS call, real audio, real DTMF, real media streaming to
 * the Analyst.
 */
export function useSoftPhone() {
  const [state, setState] = useState<PhoneState>('idle');
  const [acsUserId, setAcsUserId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [device, setDevice] = useState<DeviceCheck>({ microphone: false, checked: false });

  const agentRef = useRef<CallAgent | null>(null);
  const incomingRef = useRef<IncomingCall | null>(null);
  const ringRef = useRef<{ stop: () => void } | null>(null);
  const callRef = useRef<Call | null>(null);
  const credentialRef = useRef<AzureCommunicationTokenCredential | null>(null);
  const heartbeatRef = useRef<ReturnType<typeof setInterval> | null>(null);

  /**
   * Probe the microphone before any call is placed.
   *
   * This exists because the alternative is what shipped first: the call goes out, the
   * browser silently cannot accept it, and the user watches "Calling your device…"
   * forever with no error and no way forward. Finding out what the device can do is a
   * precondition of starting an authentication flow, not something to discover halfway
   * through one.
   */
  const checkDevice = useCallback(async (): Promise<DeviceCheck> => {
    if (typeof navigator === 'undefined' || !navigator.mediaDevices) {
      const result = { microphone: false, reason: 'This browser exposes no media devices.', checked: true };
      setDevice(result);
      return result;
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      stream.getTracks().forEach((track) => track.stop());
      const result = { microphone: true, checked: true };
      setDevice(result);
      return result;
    } catch (probeError) {
      const name = probeError instanceof Error ? probeError.name : 'Error';
      const reason =
        name === 'NotAllowedError'
          ? 'Microphone access was blocked. Allow it in your browser’s site settings and try again.'
          : name === 'NotFoundError'
            ? 'No microphone was found on this device.'
            : `Microphone unavailable (${name}).`;
      const result = { microphone: false, reason, checked: true };
      setDevice(result);
      return result;
    }
  }, []);

  /**
   * Tell the server this device is live and listening.
   *
   * Reported only after the CallAgent exists and its incomingCall handler is attached —
   * i.e. after the device can genuinely answer. Anything reported earlier would be the
   * same lie the old enrollment told.
   */
  const beat = useCallback(async (upn: string, userId: string, deviceKind: string) => {
    try {
      await fetch('/api/presence', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ upn, acsUserId: userId, deviceKind }),
      });
    } catch {
      // The next heartbeat retries; presence going stale is the correct outcome if this
      // device really has lost connectivity.
    }
  }, []);

  /**
   * A ringtone, synthesised rather than loaded.
   *
   * No audio asset to ship or fail to load, and no autoplay problem: the AudioContext is
   * created after the user has already tapped "Connect this device", which satisfies the
   * gesture requirement. Two tones at UK-ish cadence so it reads as a call rather than a
   * notification.
   */
  const startRinging = useCallback(() => {
    stopRinging();
    try {
      const AudioCtor = window.AudioContext
        ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
      if (!AudioCtor) return;

      const ctx = new AudioCtor();
      const gain = ctx.createGain();
      gain.gain.value = 0.0001;
      gain.connect(ctx.destination);

      const a = ctx.createOscillator();
      const b = ctx.createOscillator();
      a.frequency.value = 440;
      b.frequency.value = 480;
      a.connect(gain); b.connect(gain);
      a.start(); b.start();

      let on = false;
      const pulse = setInterval(() => {
        on = !on;
        gain.gain.setTargetAtTime(on ? 0.06 : 0.0001, ctx.currentTime, 0.01);
        if (on) navigator.vibrate?.([400]);
      }, 500);

      ringRef.current = {
        stop: () => {
          clearInterval(pulse);
          try { a.stop(); b.stop(); void ctx.close(); } catch { /* already closed */ }
          navigator.vibrate?.(0);
        },
      };
    } catch {
      // A silent ring is survivable; the UI still shows the incoming call.
    }
  }, []);

  const stopRinging = useCallback(() => {
    ringRef.current?.stop();
    ringRef.current = null;
  }, []);

  /**
   * Answer the ringing call.
   *
   * Called from a real tap, which is what lets the browser grant microphone capture — and
   * therefore what makes the microphone indicator appear. Accepting without that gesture
   * produces a connected call with no audio, which looks identical to a broken one.
   */
  const answer = useCallback(async () => {
    const incoming = incomingRef.current;
    if (!incoming) return false;

    stopRinging();

    try {
      // Re-probe here rather than trusting the earlier check: permission can be revoked
      // between enrolment and the call, and accepting unmuted without a microphone throws.
      const probe = await checkDevice();

      const call = await incoming.accept(
        probe.microphone ? undefined : { audioOptions: { muted: true } },
      );

      callRef.current = call;
      incomingRef.current = null;
      setState('connected');

      call.on('stateChanged', () => {
        if (call.state === 'Disconnected') {
          setState('ended');
          callRef.current = null;
        }
      });
      return true;
    } catch (acceptError) {
      setError(acceptError instanceof Error
        ? `Could not answer the call: ${acceptError.message}`
        : String(acceptError));
      setState('error');
      return false;
    }
  }, [checkDevice, stopRinging]);

  const decline = useCallback(async () => {
    stopRinging();
    try { await incomingRef.current?.reject(); } catch { /* already gone */ }
    incomingRef.current = null;
    setState('ready');
  }, [stopRinging]);

  /** Register an ACS identity for this browser so it becomes callable. */
  const register = useCallback(async (upn: string, objectId?: string, deviceKind = 'browser') => {
    setState('registering');
    setError(null);

    try {
      const response = await fetch('/api/acs/token', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ upn, objectId, deviceKind }),
      });

      if (!response.ok) {
        const detail = await response.json().catch(() => ({}));
        throw new Error(detail.error ?? `Token request failed (${response.status})`);
      }

      const { acsUserId: userId, token } = await response.json();

      const [{ AzureCommunicationTokenCredential }, { CallClient }] = await Promise.all([
        import('@azure/communication-common'),
        import('@azure/communication-calling'),
      ]);

      const credential = new AzureCommunicationTokenCredential(token);
      credentialRef.current = credential;

      const callClient = new CallClient();
      const agent = await callClient.createCallAgent(credential, { displayName: upn });
      agentRef.current = agent;

      agent.on('incomingCall', ({ incomingCall }: { incomingCall: IncomingCall }) => {
        // Deliberately NOT auto-accepted.
        //
        // Two reasons. It should feel like a phone call — a device that silently connects
        // is indistinguishable from one that did nothing, which is exactly the confusion
        // this flow kept producing. And mobile browsers gate microphone capture behind a
        // user gesture: accepting programmatically is how you end up in a call with no
        // audio and no microphone indicator, because getUserMedia was never granted for
        // that interaction.
        incomingRef.current = incomingCall;
        setState('ringing');
        startRinging();

        incomingCall.on('callEnded', () => {
          stopRinging();
          incomingRef.current = null;
          setState((current) => (current === 'ringing' ? 'ready' : current));
        });
      });

      setAcsUserId(userId);
      setState('ready');

      // First beat immediately, then every 10s. StaleAfter on the server is 30s, so two
      // consecutive misses are tolerated before this device stops counting as reachable.
      void beat(upn, userId, deviceKind);
      if (heartbeatRef.current) clearInterval(heartbeatRef.current);
      heartbeatRef.current = setInterval(() => void beat(upn, userId, deviceKind), 10_000);

      return userId as string;
    } catch (registerError) {
      setError(registerError instanceof Error ? registerError.message : String(registerError));
      setState('error');
      return null;
    }
  }, [beat, startRinging, stopRinging]);

  /**
   * Send a DTMF tone into the live call.
   *
   * The proof of possession: the digits travel over the call leg, not the web session, so
   * entering them demonstrates control of the phone endpoint rather than of the browser tab.
   */
  const sendDigit = useCallback(async (digit: string) => {
    const call = callRef.current;
    if (!call) return false;

    try {
      // The SDK's DtmfTone type uses word names — 'Num0'…'Num9' — not bare digits.
      await call.sendDtmf(`Num${digit}` as never);
      return true;
    } catch {
      return false;
    }
  }, []);

  const hangUp = useCallback(async () => {
    try {
      await callRef.current?.hangUp();
    } catch {
      // Already gone.
    }
    callRef.current = null;
    setState((current) => (current === 'error' ? 'error' : 'ready'));
  }, []);

  useEffect(() => () => {
    if (heartbeatRef.current) clearInterval(heartbeatRef.current);
    ringRef.current?.stop();
    // Dispose on unmount so the identity does not linger as callable after the user has
    // navigated away.
    void callRef.current?.hangUp().catch(() => {});
    void agentRef.current?.dispose().catch(() => {});
    credentialRef.current?.dispose();
  }, []);

  return { state, acsUserId, error, device, checkDevice, register, answer, decline, sendDigit, hangUp };
}
