/**
 * Fluent-style line icons, drawn inline.
 *
 * Segoe Fluent Icons is a licensed font and not redistributable, so these are hand-drawn
 * equivalents on the same 20px grid with the same 1.5px stroke weight. Inline SVG also
 * means no icon-font request on the critical path and no flash of missing glyphs.
 */
type P = { size?: number; className?: string };

const svg = (path: React.ReactNode, size = 16, className?: string) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 20 20"
    fill="none"
    stroke="currentColor"
    strokeWidth="1.5"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
    className={className}
  >
    {path}
  </svg>
);

export const IconRefresh = ({ size, className }: P) =>
  svg(<><path d="M17 10a7 7 0 1 1-2.05-4.95" /><path d="M17 3v4h-4" /></>, size, className);

export const IconShield = ({ size, className }: P) =>
  svg(<path d="M10 2.5 4 5v4.5c0 3.6 2.4 6.9 6 8 3.6-1.1 6-4.4 6-8V5l-6-2.5Z" />, size, className);

export const IconGrid = ({ size, className }: P) =>
  svg(<><rect x="3" y="3" width="5.5" height="5.5" /><rect x="11.5" y="3" width="5.5" height="5.5" /><rect x="3" y="11.5" width="5.5" height="5.5" /><rect x="11.5" y="11.5" width="5.5" height="5.5" /></>, size, className);

export const IconPhone = ({ size, className }: P) =>
  svg(<path d="M6.5 3h-2A1.5 1.5 0 0 0 3 4.6C3 11 9 17 15.4 17a1.5 1.5 0 0 0 1.6-1.5v-2l-3.5-1.2-1.6 1.7a11 11 0 0 1-4.9-4.9l1.7-1.6L6.5 3Z" />, size, className);

export const IconSiem = ({ size, className }: P) =>
  svg(<><path d="M3 15V9M7.5 15V5M12 15v-4M16.5 15V7" /><path d="M2.5 17.5h15" /></>, size, className);

export const IconResources = ({ size, className }: P) =>
  svg(<><path d="M10 2.5 17 6.5v7L10 17.5 3 13.5v-7l7-4Z" /><path d="M3 6.5 10 10.5l7-4M10 10.5v7" /></>, size, className);

export const IconWarning = ({ size, className }: P) =>
  svg(<><path d="M10 3 2.5 16.5h15L10 3Z" /><path d="M10 8v3.5M10 14h.01" /></>, size, className);

export const IconError = ({ size, className }: P) =>
  svg(<><circle cx="10" cy="10" r="7.5" /><path d="M10 6v4.5M10 13.5h.01" /></>, size, className);

export const IconInfo = ({ size, className }: P) =>
  svg(<><circle cx="10" cy="10" r="7.5" /><path d="M10 9.5V14M10 6.5h.01" /></>, size, className);

export const IconCheck = ({ size, className }: P) =>
  svg(<><circle cx="10" cy="10" r="7.5" /><path d="M6.5 10.2 9 12.6l4.5-4.8" /></>, size, className);

export const IconPlay = ({ size, className }: P) =>
  svg(<path d="M6.5 4.5v11l9-5.5-9-5.5Z" />, size, className);

export const IconSearch = ({ size, className }: P) =>
  svg(<><circle cx="9" cy="9" r="5.5" /><path d="M13 13l4 4" /></>, size, className);

export const IconChevron = ({ size, className }: P) =>
  svg(<path d="M7 4.5 12.5 10 7 15.5" />, size, className);

export const IconClock = ({ size, className }: P) =>
  svg(<><circle cx="10" cy="10" r="7.5" /><path d="M10 5.5V10l3 1.8" /></>, size, className);

export const IconSort = ({ size, className }: P) =>
  svg(<path d="M6 8l4-4 4 4M6 12l4 4 4-4" />, size, className);

export const IconStop = ({ size, className }: P) =>
  svg(<rect x="5.5" y="5.5" width="9" height="9" rx="1" />, size, className);
