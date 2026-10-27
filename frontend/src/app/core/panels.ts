import { Injectable, computed, signal } from '@angular/core';

export type PanelKind = 'trade' | 'account' | 'risk' | 'strategy' | 'gemini' | 'patterns' | 'holidays' | 'token' | 'notifications';

/** One drawer at a time over the dashboard; `id` is the trade or account it targets (none = create). */
@Injectable({ providedIn: 'root' })
export class PanelService {
  readonly active = signal<{ kind: PanelKind; id: number | null } | null>(null);

  open(kind: PanelKind, id: number | null = null): void { this.active.set({ kind, id }); }
  close(): void { this.active.set(null); }

  isOpen(kind: PanelKind) { return computed(() => this.active()?.kind === kind); }
  idFor(kind: PanelKind) { return computed(() => (this.active()?.kind === kind ? this.active()!.id : null)); }
}
