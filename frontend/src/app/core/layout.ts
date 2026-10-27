import { DOCUMENT, DestroyRef, Injectable, inject, signal } from '@angular/core';

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
