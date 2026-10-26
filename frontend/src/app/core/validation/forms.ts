import { Injectable, Signal, inject, signal } from '@angular/core';
import { AbstractControl, FormControl, FormGroup, ValidationErrors, ValidatorFn } from '@angular/forms';
import { Observable, firstValueFrom } from 'rxjs';
import { ApiService, apiErrors } from '../api';
import { ApiError } from '../models';
import { FieldRule, FormRuleSet, ValidationRulesDocument, Violation, checkField, fieldMessage, findForm, validateForm } from './rules';

/** Loads GET /api/validation/rules once per session; every drawer validates against it. */
@Injectable({ providedIn: 'root' })
export class RulesService {
  private readonly api = inject(ApiService);
  private loading: Promise<ValidationRulesDocument> | null = null;
  readonly doc = signal<ValidationRulesDocument | null>(null);

  load(): Promise<ValidationRulesDocument> {
    this.loading ??= firstValueFrom(this.api.rules()).then((d) => {
      this.doc.set(d);
      return d;
    }).catch((e: unknown) => {
      this.loading = null;
      throw e;
    });
    return this.loading;
  }

  ruleMessage(code: string): string | null {
    return this.doc()?.rules.find((r) => r.code === code)?.message ?? null;
  }
}

export interface RuleForm {
  group: FormGroup<Record<string, FormControl<unknown>>>;
  set: FormRuleSet;
  /** Cross-field violations currently failing (form-level messages). */
  crossErrors: () => Violation[];
  /** Bumps on every value/status/touch change so OnPush templates re-render. */
  tick: Signal<number>;
  dispose: () => void;
}

/**
 * Reactive form whose controls carry the served field rules and whose group runs the CX_* rules,
 * so the drawer blocks exactly what the API would reject. `extra` adds controls without field rules
 * (write-only secrets, flags used by cross rules such as hasApiKey).
 */
export function buildRuleForm(doc: ValidationRulesDocument, form: string, initial: Record<string, unknown>, extra: string[] = []): RuleForm {
  const set = findForm(doc, form);
  const controls: Record<string, FormControl<unknown>> = {};
  for (const rule of set.fields) controls[rule.field] = new FormControl<unknown>(normalise(rule, initial[rule.field]), fieldValidator(rule));
  for (const key of extra) controls[key] = new FormControl<unknown>(initial[key] ?? null);
  const group = new FormGroup(controls, { validators: crossValidator(doc, form, set) });
  const tick = signal(0);
  const sub = group.events.subscribe(() => tick.update((n) => n + 1));
  return {
    group, set, tick,
    crossErrors: () => (group.errors?.['cross'] as Violation[] | undefined) ?? [],
    dispose: () => sub.unsubscribe(),
  };
}

export function fieldValidator(rule: FieldRule): ValidatorFn {
  return (c: AbstractControl): ValidationErrors | null => {
    const code = checkField(rule, coerce(rule, c.value));
    return code ? { rule: { code, message: fieldMessage(code, rule) } } : null;
  };
}

function crossValidator(doc: ValidationRulesDocument, form: string, set: FormRuleSet): ValidatorFn {
  const crossCodes = new Set(set.crossFieldRules.map((r) => r.code));
  return (g: AbstractControl): ValidationErrors | null => {
    const failing = validateForm(doc, form, formValues(set, g.getRawValue() as Record<string, unknown>)).filter((v) => crossCodes.has(v.code));
    return failing.length ? { cross: failing } : null;
  };
}

/** Values in the shape the API validates (numbers as numbers, blank optional text as null). */
export function formValues(set: FormRuleSet, raw: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = { ...raw };
  for (const rule of set.fields) out[rule.field] = coerce(rule, raw[rule.field]);
  return out;
}

function coerce(rule: FieldRule, v: unknown): unknown {
  if ((rule.type === 'number' || rule.type === 'integer') && typeof v === 'string' && v.trim() !== '' && !Number.isNaN(Number(v))) return Number(v);
  if (rule.type === 'time' && typeof v === 'string' && /^\d{2}:\d{2}$/.test(v)) return `${v}:00`;
  return v;
}

function normalise(rule: FieldRule, v: unknown): unknown {
  if (rule.type === 'time' && typeof v === 'string') return v.length === 5 ? `${v}:00` : v;
  return v ?? (rule.type === 'boolean' ? false : null);
}

/** Message for a control's first error: field rule, or a server error placed by applyServerErrors. */
export function controlError(c: AbstractControl | null): string | null {
  if (!c || !c.errors) return null;
  const e = c.errors;
  return (e['rule'] as { message: string } | undefined)?.message ?? (e['server'] as string | undefined) ?? null;
}

/** Puts API field errors on their controls; returns the ones that belong to the whole form. */
export function applyServerErrors(group: FormGroup, errors: ApiError[]): ApiError[] {
  const formLevel: ApiError[] = [];
  for (const e of errors) {
    const control = e.field ? group.get(e.field) : null;
    if (control) {
      control.setErrors({ ...(control.errors ?? {}), server: e.message });
      control.markAsTouched();
    } else formLevel.push(e);
  }
  return formLevel;
}

/** Drawer form lifecycle: holds the current RuleForm, blocks invalid submits, maps API errors back onto fields. */
export class FormSession {
  readonly form = signal<RuleForm | null>(null);
  readonly saving = signal(false);
  readonly serverErrors = signal<ApiError[]>([]);
  readonly loadError = signal<string | null>(null);

  set(f: RuleForm | null): void {
    this.form()?.dispose();
    this.form.set(f);
    this.serverErrors.set([]);
    this.loadError.set(null);
  }

  /** Resolves to the API result, or null when the form is invalid or the API rejected it. */
  async submit<T>(send: (values: Record<string, unknown>) => Observable<T>): Promise<T | null> {
    const f = this.form();
    if (!f) return null;
    f.group.markAllAsTouched();
    if (f.group.invalid) return null;
    this.saving.set(true);
    this.serverErrors.set([]);
    try {
      return await firstValueFrom(send(formValues(f.set, f.group.getRawValue() as Record<string, unknown>)));
    } catch (e) {
      this.serverErrors.set(applyServerErrors(f.group, apiErrors(e)));
      return null;
    } finally {
      this.saving.set(false);
    }
  }
}
