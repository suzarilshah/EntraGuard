'use client';

import { useState } from 'react';
import { IconChevron } from './Icons';

export interface EssentialItem {
  label: string;
  value: React.ReactNode;
}

/**
 * The Azure "Essentials" panel — the collapsible key/value grid that heads every resource
 * blade in the portal.
 *
 * It is the first thing an Azure operator reads, and it answers the orientation questions
 * before any chart does: which subscription, which tenant, what state, what model. Kept
 * expanded by default because on this page that context is the point, not a detail to
 * unfold.
 */
export function Essentials({ items }: { items: EssentialItem[] }) {
  const [open, setOpen] = useState(true);

  return (
    <div className="az-essentials">
      <button
        className="az-essentials-toggle"
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        type="button"
      >
        <span style={{ transform: open ? 'rotate(90deg)' : 'none', display: 'grid', transition: 'transform 120ms' }}>
          <IconChevron size={12} />
        </span>
        Essentials
      </button>

      {open && (
        <div className="az-essentials-body">
          {items.map((item) => (
            <dl className="az-kv" key={item.label}>
              <dt>{item.label}</dt>
              <dd>{item.value}</dd>
            </dl>
          ))}
        </div>
      )}
    </div>
  );
}
