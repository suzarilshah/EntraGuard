'use client';

import { useEffect, useState, useTransition } from 'react';
import { useRouter, usePathname, useSearchParams } from 'next/navigation';
import { IconRefresh, IconClock, IconPlay, IconStop } from './Icons';

/**
 * The Azure command bar — refresh, auto-refresh, and a time-range picker.
 *
 * Every control here does real work. Refresh re-runs the server components (and therefore
 * the live Azure queries); the time range is held in the URL so a page is shareable and
 * survives a reload; auto-refresh drives the same path on an interval.
 *
 * Auto-refresh defaults to OFF. Each tick re-issues every Azure query on the page, and a
 * dashboard left open on a wall would otherwise quietly bill Log Analytics queries all
 * week.
 */
export function CommandBar({
  timeRange = true,
  children,
}: {
  timeRange?: boolean;
  children?: React.ReactNode;
}) {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const [isPending, startTransition] = useTransition();
  const [auto, setAuto] = useState(false);
  const [lastRefresh, setLastRefresh] = useState<string>('');

  const hours = params.get('hours') ?? '24';

  const refresh = () => {
    startTransition(() => {
      router.refresh();
      setLastRefresh(new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }));
    });
  };

  useEffect(() => {
    if (!auto) return;
    const timer = setInterval(refresh, 30_000);
    return () => clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auto]);

  const setHours = (value: string) => {
    const next = new URLSearchParams(params.toString());
    next.set('hours', value);
    router.push(`${pathname}?${next.toString()}`);
  };

  return (
    <div className="az-commandbar">
      <button className="az-cmd" onClick={refresh} disabled={isPending} type="button">
        <IconRefresh size={15} className={isPending ? 'az-spin' : undefined} />
        {isPending ? 'Refreshing…' : 'Refresh'}
      </button>

      <button
        className={`az-cmd${auto ? ' is-on' : ''}`}
        onClick={() => setAuto((on) => !on)}
        type="button"
        title="Re-run every query on this page every 30 seconds"
      >
        {auto ? <IconStop size={15} /> : <IconPlay size={15} />}
        Auto-refresh {auto ? 'on' : 'off'}
      </button>

      {timeRange && (
        <>
          <span className="az-cmd-sep" aria-hidden="true" />
          <span className="az-cmd" style={{ cursor: 'default' }}>
            <IconClock size={15} />
            Time range
          </span>
          <select
            className="az-select"
            value={hours}
            onChange={(event) => setHours(event.target.value)}
            aria-label="Time range"
          >
            <option value="1">Last hour</option>
            <option value="6">Last 6 hours</option>
            <option value="24">Last 24 hours</option>
            <option value="168">Last 7 days</option>
            <option value="720">Last 30 days</option>
          </select>
        </>
      )}

      {children && <><span className="az-cmd-sep" aria-hidden="true" />{children}</>}

      {lastRefresh && (
        <span style={{ marginLeft: 'auto', fontSize: 12, color: 'var(--az-text-3)' }}>
          Updated {lastRefresh}
        </span>
      )}
    </div>
  );
}
