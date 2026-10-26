import { Pipe, PipeTransform } from '@angular/core';

const inr = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2, maximumFractionDigits: 2 });
const inr0 = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 });
const price = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const istTime = new Intl.DateTimeFormat('en-IN', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
const istDateTime = new Intl.DateTimeFormat('en-IN', { timeZone: 'Asia/Kolkata', day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false });

export const IST_OFFSET_SECONDS = 19800;

export function formatInr(v: number | null | undefined, whole = false): string {
  return v === null || v === undefined ? '—' : (whole ? inr0 : inr).format(v);
}

export function formatSignedInr(v: number | null | undefined): string {
  if (v === null || v === undefined) return '—';
  return `${v > 0 ? '+' : ''}${inr.format(v)}`;
}

/** "Today" in IST as yyyy-MM-dd (the API's trading-date format). */
export function istToday(now = new Date()): string {
  return new Date(now.getTime() + IST_OFFSET_SECONDS * 1000).toISOString().slice(0, 10);
}

/** Seconds as h:mm:ss for token countdowns. */
export function formatDuration(totalSeconds: number | null | undefined): string {
  if (totalSeconds === null || totalSeconds === undefined) return '—';
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  return `${h}:${String(m).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
}

@Pipe({ name: 'inr' })
export class InrPipe implements PipeTransform {
  transform(v: number | null | undefined, mode: 'plain' | 'signed' | 'whole' = 'plain'): string {
    return mode === 'signed' ? formatSignedInr(v) : formatInr(v, mode === 'whole');
  }
}

@Pipe({ name: 'price' })
export class PricePipe implements PipeTransform {
  transform(v: number | null | undefined): string { return v === null || v === undefined ? '—' : price.format(v); }
}

@Pipe({ name: 'ist' })
export class IstPipe implements PipeTransform {
  transform(v: string | null | undefined, withDate = false): string {
    if (!v) return '—';
    const d = new Date(v);
    return Number.isNaN(d.getTime()) ? '—' : (withDate ? istDateTime : istTime).format(d);
  }
}

/** CSS class for profit/loss colouring. */
export function pnlClass(v: number | null | undefined): string {
  return !v ? 'text-body-secondary' : v > 0 ? 'text-success' : 'text-danger';
}
