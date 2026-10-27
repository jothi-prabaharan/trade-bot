import { Component, ElementRef, Injectable, computed, effect, inject, input, model, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FaIconComponent } from '@fortawesome/angular-fontawesome';
import { faCircleExclamation, faTriangleExclamation, faXmark } from '@fortawesome/free-solid-svg-icons';
import { ToastService } from '../core/ui-state';

/** Bootstrap offcanvas driven by a signal (no Bootstrap JS). */
@Component({
  selector: 'tb-drawer',
  host: { '(document:keydown.escape)': 'escape($event)' },
  template: `
    @if (open()) {
      <div class="offcanvas offcanvas-end show tb-drawer" [style.--bs-offcanvas-width]="width()" tabindex="-1" role="dialog" aria-modal="true" [attr.aria-label]="title()">
        <div class="offcanvas-header border-bottom">
          <h5 class="offcanvas-title text-truncate">{{ title() }}</h5>
          <button type="button" class="btn-close" aria-label="Close" (click)="close()"></button>
        </div>
        <div class="offcanvas-body"><ng-content /></div>
        <div class="tb-drawer-footer border-top p-3 d-flex flex-wrap gap-2 justify-content-end align-items-center"><ng-content select="[footer]" /></div>
      </div>
      <div class="offcanvas-backdrop show" (click)="close()"></div>
    }
  `,
})
export class Drawer {
  protected readonly confirm = inject(ConfirmService);
  readonly open = model(false);
  readonly title = input('');
  readonly width = input('560px');
  readonly closed = output<void>();

  close(): void {
    this.open.set(false);
    this.closed.emit();
  }

  // Returns nothing on purpose: an Angular listener that returns false calls preventDefault().
  // A confirmation dialog on top takes the Escape (see ConfirmHost).
  protected escape(e: Event): void {
    if (this.open() && !e.defaultPrevented && !this.confirm.current()) this.close();
  }
}

export interface ConfirmRequest {
  title: string;
  message: string;
  confirmLabel: string;
  danger?: boolean;
  /** User must type exactly this (e.g. KILL or the client ID). */
  typeToConfirm?: string;
  askReason?: boolean;
}
export interface ConfirmResult { reason: string }

@Injectable({ providedIn: 'root' })
export class ConfirmService {
  readonly current = signal<(ConfirmRequest & { resolve: (r: ConfirmResult | null) => void }) | null>(null);

  ask(req: ConfirmRequest): Promise<ConfirmResult | null> {
    this.current()?.resolve(null);
    return new Promise((resolve) => this.current.set({ ...req, resolve }));
  }
}

@Component({
  selector: 'tb-confirm-host',
  imports: [FormsModule, FaIconComponent],
  host: { '(document:keydown.escape)': 'escape($event)' },
  template: `
    @if (svc.current(); as req) {
      <div #dialog class="modal d-block" tabindex="-1" role="dialog" aria-modal="true" [attr.aria-label]="req.title">
        <div class="modal-dialog modal-dialog-centered">
          <div class="modal-content">
            <div class="modal-header">
              <h5 class="modal-title">
                <fa-icon [icon]="req.danger ? icons.danger : icons.warn" [class]="req.danger ? 'text-danger me-2' : 'text-warning me-2'" />{{ req.title }}
              </h5>
              <button type="button" class="btn-close" aria-label="Cancel" (click)="finish(false)"></button>
            </div>
            <div class="modal-body">
              <p class="mb-3" style="white-space: pre-line">{{ req.message }}</p>
              @if (req.typeToConfirm) {
                <label class="form-label" for="tb-confirm-typed">Type <code class="user-select-all">{{ req.typeToConfirm }}</code> to confirm</label>
                <input id="tb-confirm-typed" class="form-control mb-3" autocomplete="off" [(ngModel)]="typed" name="typed" />
              }
              @if (req.askReason) {
                <label class="form-label" for="tb-confirm-reason">Reason</label>
                <textarea id="tb-confirm-reason" class="form-control" rows="2" maxlength="300" [(ngModel)]="reason" name="reason"></textarea>
              }
            </div>
            <div class="modal-footer">
              <button type="button" class="btn btn-outline-secondary" data-autofocus (click)="finish(false)">Cancel</button>
              <button type="button" class="btn" [class]="req.danger ? 'btn-danger' : 'btn-warning'" [disabled]="!ready()" (click)="finish(true)">{{ req.confirmLabel }}</button>
            </div>
          </div>
        </div>
      </div>
      <div class="modal-backdrop show"></div>
    }
  `,
})
export class ConfirmHost {
  protected readonly icons = { danger: faCircleExclamation, warn: faTriangleExclamation };
  protected readonly typedValue = signal('');
  protected readonly reasonValue = signal('');

  private readonly dialog = viewChild<ElementRef<HTMLElement>>('dialog');

  constructor(protected readonly svc: ConfirmService) {
    // Focus moves into the dialog: the typed confirmation if there is one, else Cancel (the safe choice)
    effect(() => {
      const el = this.dialog()?.nativeElement;
      el?.querySelector<HTMLElement>('#tb-confirm-typed, [data-autofocus]')?.focus();
    });
  }

  protected get typed(): string { return this.typedValue(); }
  protected set typed(v: string) { this.typedValue.set(v); }
  protected get reason(): string { return this.reasonValue(); }
  protected set reason(v: string) { this.reasonValue.set(v); }

  protected readonly ready = computed(() => {
    const req = this.svc.current();
    if (!req) return false;
    if (req.typeToConfirm && this.typedValue().trim() !== req.typeToConfirm) return false;
    return !req.askReason || this.reasonValue().trim().length > 0;
  });

  protected escape(e: Event): void {
    if (!this.svc.current()) return;
    // Tells the drawer underneath that this Escape is taken
    e.preventDefault();
    this.finish(false);
  }

  protected finish(ok: boolean): void {
    const req = this.svc.current();
    if (!req) return;
    if (ok && !this.ready()) return;
    this.svc.current.set(null);
    req.resolve(ok ? { reason: this.reasonValue().trim() } : null);
    this.typedValue.set('');
    this.reasonValue.set('');
  }
}

@Component({
  selector: 'tb-toasts',
  imports: [FaIconComponent],
  template: `
    <div class="toast-container position-fixed bottom-0 start-0 p-3 tb-toasts" aria-live="polite">
      @for (t of svc.toasts(); track t.id) {
        <div class="toast show border-0 tb-toast" [class]="'tb-toast-' + t.tone" role="status">
          <div class="toast-header">
            <strong class="me-auto">{{ t.title }}</strong>
            <button type="button" class="btn btn-sm p-0 border-0 tb-toast-close" aria-label="Dismiss" (click)="svc.dismiss(t.id)"><fa-icon [icon]="close" /></button>
          </div>
          <div class="toast-body">{{ t.body }}</div>
        </div>
      }
    </div>
  `,
})
export class ToastHost {
  protected readonly close = faXmark;
  constructor(protected readonly svc: ToastService) {}
}
