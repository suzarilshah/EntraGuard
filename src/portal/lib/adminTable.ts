export function fullCell(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'object' && 'label' in value && typeof value.label === 'string') return value.label;
  if (value instanceof Date) return value.toISOString();
  if (Array.isArray(value)) return value.map(fullCell).join(', ');
  if (typeof value === 'object') return JSON.stringify(value, null, 2);
  return String(value);
}
export function tableCsv(labels: string[], rows: unknown[][]): string {
  const field = (value: unknown) => {
    let text = fullCell(value);
    // Directory names and incident titles are untrusted spreadsheet input.
    if (typeof value !== 'number' && /^[\s]*[=+\-@]/.test(text)) text = `'${text}`;
    return `"${text.replaceAll('"', '""')}"`;
  };
  return [labels, ...rows].map(row => row.map(field).join(',')).join('\r\n');
}
export function safeControlPlaneLink(value: unknown): value is { label: string; url: string } {
  if (!value || typeof value !== 'object' || !('label' in value) || !('url' in value)
      || typeof value.label !== 'string' || typeof value.url !== 'string') return false;
  try { const url = new URL(value.url); return url.protocol === 'https:' && ['portal.azure.com', 'entra.microsoft.com'].includes(url.hostname) && !url.username && !url.password; }
  catch { return false; }
}
