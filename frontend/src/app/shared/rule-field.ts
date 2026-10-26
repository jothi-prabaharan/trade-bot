import { Component, computed, input } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { ApiError } from '../core/models';
import { RuleForm, controlError } from '../core/validation/forms';

let nextId = 0;

/** One form field rendered from its served rule: input type, step, bounds and messages all come from the API. */
@Component({
  selector: 'tb-rule-field',
  imports: [ReactiveFormsModule],
  host: { '[class]': 'col()' },
  template: `
    @let r = rule();
    @let c = control();
    @let t = form().tick();
    @if (r && c) {
      @if (r.type === 'boolean') {
        <div class="form-check form-switch mt-2">
          <input class="form-check-input" type="checkbox" [id]="id" [formControl]="c" />
          <label class="form-check-label" [for]="id">{{ label() }}</label>
        </div>
      } @else {
        <label class="form-label small mb-1" [for]="id">{{ label() }} @if (!r.required) { <span class="text-body-secondary">(optional)</span> }</label>
        <div class="input-group input-group-sm has-validation">
          @switch (r.type) {
            @case ('enum') {
              <select class="form-select" [id]="id" [formControl]="c" [class.is-invalid]="invalid(t)">
                @for (v of r.allowedValues ?? []; track v) { <option [value]="v">{{ v }}</option> }
              </select>
            }
            @case ('json') { <textarea class="form-control font-monospace" rows="4" [id]="id" [formControl]="c" [class.is-invalid]="invalid(t)"></textarea> }
            @case ('time') { <input class="form-control" type="time" step="1" [id]="id" [formControl]="c" [class.is-invalid]="invalid(t)" [readonly]="readonly()" /> }
            @case ('date') { <input class="form-control" type="date" [id]="id" [formControl]="c" [class.is-invalid]="invalid(t)" [readonly]="readonly()" /> }
            @case ('string') {
              <input class="form-control" [type]="secret() ? 'password' : 'text'" [id]="id" [formControl]="c" [class.is-invalid]="invalid(t)"
                [readonly]="readonly()" [attr.maxlength]="r.maxLength ?? null" [attr.placeholder]="placeholder() || null" autocomplete="off" />
            }
            @default {
              <input class="form-control text-end" type="number" [id]="id" [formControl]="c" [class.is-invalid]="invalid(t)" [readonly]="readonly()"
                [attr.step]="step()" [attr.min]="r.min ?? null" [attr.max]="r.max !== undefined && r.max < 1e15 ? r.max : null" />
            }
          }
          @if (suffix()) { <span class="input-group-text">{{ suffix() }}</span> }
          @if (invalid(t)) { <div class="invalid-feedback">{{ message() }}</div> }
        </div>
        @if (hint()) { <div class="form-text">{{ hint() }}</div> }
      }
    }
  `,
})
export class RuleField {
  readonly form = input.required<RuleForm>();
  readonly name = input.required<string>();
  readonly label = input.required<string>();
  readonly hint = input('');
  readonly suffix = input('');
  readonly placeholder = input('');
  readonly secret = input(false);
  readonly readonly = input(false);
  readonly col = input('col-12 col-sm-6');

  protected readonly id = `tb-field-${++nextId}`;
  protected readonly rule = computed(() => this.form().set.fields.find((f) => f.field === this.name()) ?? null);
  protected readonly control = computed(() => this.form().group.controls[this.name()] as FormControl<unknown> | undefined);
  protected readonly step = computed(() => {
    const r = this.rule();
    return r?.type === 'integer' ? 1 : r?.scale !== undefined ? 1 / 10 ** Math.min(r.scale, 2) : 'any';
  });

  protected invalid(_tick: number): boolean {
    const c = this.control();
    return !!c && (c.touched || c.dirty) && !!c.errors;
  }
  protected message(): string | null { return controlError(this.control() ?? null); }
}

/** Plain input for controls without field rules (write-only secrets such as the API key). */
@Component({
  selector: 'tb-secret-field',
  imports: [ReactiveFormsModule],
  host: { '[class]': 'col()' },
  template: `
    <label class="form-label small mb-1" [for]="id">{{ label() }}</label>
    <input class="form-control form-control-sm" type="password" autocomplete="new-password" [id]="id" [formControl]="control()"
      [placeholder]="stored() ? 'Stored · leave blank to keep' : 'Not set'" />
  `,
})
export class SecretField {
  readonly control = input.required<FormControl<unknown>>();
  readonly label = input.required<string>();
  readonly stored = input(false);
  readonly col = input('col-12 col-sm-6');
  protected readonly id = `tb-secret-${++nextId}`;
}

/** Cross-field (CX_*) and server form-level errors. */
@Component({
  selector: 'tb-form-errors',
  host: { class: 'w-100' },
  template: `
    @let t = form().tick();
    @let cross = form().crossErrors();
    @if (show(t) && (cross.length || server().length)) {
      <div class="alert alert-danger py-2 small mb-0" role="alert">
        <ul class="mb-0 ps-3">
          @for (e of cross; track e.code) { <li>{{ e.message }} <span class="text-body-secondary">({{ e.code }})</span></li> }
          @for (e of server(); track e.code + e.message) { <li>{{ e.message }} <span class="text-body-secondary">({{ e.code }})</span></li> }
        </ul>
      </div>
    }
  `,
})
export class FormErrors {
  readonly form = input.required<RuleForm>();
  readonly server = input<ApiError[]>([]);
  protected show(_tick: number): boolean { return this.form().group.dirty || this.form().group.touched || this.server().length > 0; }
}
