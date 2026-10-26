import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ThemeService } from './core/ui-state';
import { ConfirmHost, ToastHost } from './shared/ui';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastHost, ConfirmHost],
  template: `<router-outlet /><tb-toasts /><tb-confirm-host />`,
})
export class App {
  // Applies the stored colour mode before the first view renders
  protected readonly theme = inject(ThemeService);
}
