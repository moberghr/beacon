const SHORT_DATE = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
const DATE_TIME = new Intl.DateTimeFormat(undefined, {
  year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
});

export function formatDate(value: string | Date | null | undefined): string {
  if (value == null) return '—';
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? '—' : SHORT_DATE.format(d);
}

export function formatDateTime(value: string | Date | null | undefined): string {
  if (value == null) return '—';
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? '—' : DATE_TIME.format(d);
}

const RELATIVE = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

export function formatRelativeTime(value: string | Date | null | undefined): string {
  if (value == null) return '—';
  const d = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(d.getTime())) return '—';

  const diffMs = d.getTime() - Date.now();
  const diffSec = Math.round(diffMs / 1000);

  if (Math.abs(diffSec) < 60) return RELATIVE.format(diffSec, 'second');
  const diffMin = Math.round(diffSec / 60);
  if (Math.abs(diffMin) < 60) return RELATIVE.format(diffMin, 'minute');
  const diffHr = Math.round(diffMin / 60);
  if (Math.abs(diffHr) < 24) return RELATIVE.format(diffHr, 'hour');
  const diffDay = Math.round(diffHr / 24);
  if (Math.abs(diffDay) < 30) return RELATIVE.format(diffDay, 'day');
  const diffMonth = Math.round(diffDay / 30);
  if (Math.abs(diffMonth) < 12) return RELATIVE.format(diffMonth, 'month');
  return RELATIVE.format(Math.round(diffMonth / 12), 'year');
}

export function formatNumber(value: number | null | undefined): string {
  if (value == null) return '—';
  return value.toLocaleString();
}

export function formatPercentage(value: number | null | undefined, fractionDigits = 1): string {
  if (value == null) return '—';
  return `${value.toFixed(fractionDigits)}%`;
}
