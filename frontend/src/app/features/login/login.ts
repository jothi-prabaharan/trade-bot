import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faChartLine, faRightToBracket } from '@fortawesome/free-solid-svg-icons';
import { firstValueFrom } from 'rxjs';
import { ApiService, apiErrorText } from '../../core/api';
import { AuthService } from '../../core/auth';

@Component({
  selector: 'tb-login',
  imports: [FormsModule, FaIconComponent],
  template: `
    <main class="min-vh-100 d-flex align-items-center justify-content-center p-3">
      <form class="tb-panel p-4 w-100" style="max-width: 380px" (ngSubmit)="submit()" #f="ngForm">
        <h1 class="h4 mb-1"><fa-icon [icon]="icons.logo" class="text-success me-2" />TradeBot</h1>
        <p class="text-body-secondary mb-4">Sign in to the trading dashboard</p>
        <label class="form-label" for="username">Username</label>
        <input id="username" name="username" class="form-control mb-3" autocomplete="username" required [(ngModel)]="username" />
        <label class="form-label" for="password">Password</label>
        <input id="password" name="password" type="password" class="form-control mb-3" autocomplete="current-password" required [(ngModel)]="password" />
        @if (error()) { <div class="alert alert-danger py-2" role="alert">{{ error() }}</div> }
        <button type="submit" class="btn btn-success w-100" [disabled]="busy() || !f.valid">
          <fa-icon [icon]="icons.go" class="me-2" />{{ busy() ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>
    </main>
  `,
})
export class Login {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly icons = { logo: faChartLine, go: faRightToBracket };
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected username = 'admin';
  protected password = '';

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.auth.store(await firstValueFrom(this.api.login(this.username, this.password)));
      await this.router.navigate(['/']);
    } catch (e) {
      this.error.set(apiErrorText(e));
    } finally {
      this.busy.set(false);
    }
  }
}
