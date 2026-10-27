import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { PwaService } from './core/pwa';
import { ThemeService } from './core/ui-state';
import { ConfirmHost, ToastHost } from './shared/ui';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastHost, ConfirmHost],
  template: `
    <router-outlet /><tb-toasts /><tb-confirm-host />
    @if (pwa.update() !== 'none') {
      <div class="alert alert-info shadow d-flex align-items-center gap-3 py-2 px-3 position-fixed top-0 start-50 translate-middle-x mt-2 tb-update" role="status">
        <span>{{ pwa.update() === 'ready' ? 'A new version of TradeBot is ready.' : 'TradeBot needs a reload to keep working.' }}</span>
        <button type="button" class="btn btn-sm btn-primary" (click)="pwa.reload()">Reload</button>
      </div>
    }
  `,
})
export class App {
  // Applies the stored colour mode before the first view renders
  protected readonly theme = inject(ThemeService);
  // Created at start-up so the browser's one-time install prompt is not missed
  protected readonly pwa = inject(PwaService);
}
