import { DOCUMENT, DestroyRef, Injectable, WritableSignal, effect, inject, signal } from '@angular/core';

export type ListView = 'compact' | 'detail';

/** Phone layout: below Bootstrap's md breakpoint the dashboard shows lists instead of tables. */
@Injectable({ providedIn: 'root' })
export class LayoutService {
  private readonly query = inject(DOCUMENT).defaultView?.matchMedia?.('(max-width: 767.98px)');
  readonly phone = signal(this.query?.matches ?? false);

  constructor() {
    const onChange = (e: MediaQueryListEvent) => this.phone.set(e.matches);
    this.query?.addEventListener('change', onChange);
    inject(DestroyRef).onDestroy(() => this.query?.removeEventListener('change', onChange));
  }
}

/** A list's compact/detail choice, remembered in this browser (call from a field initializer). */
export function rememberedView(key: string): WritableSignal<ListView> {
  const storageKey = `tradebot.view.${key}`;
  let stored: string | null = null;
  try { stored = localStorage.getItem(storageKey); } catch { /* storage blocked */ }
  const view = signal<ListView>(stored === 'detail' ? 'detail' : 'compact');
  effect(() => {
    try { localStorage.setItem(storageKey, view()); } catch { /* storage blocked: keep in memory */ }
  });
  return view;
}
