// TypeScript twin of TradeBot.Application.Validation.Forms.FormValidator. Rule data comes from
// GET /api/validation/rules; each CX_* rule is implemented once here. Both sides run the same
// shared/validation/validation-vectors.json.

export type FieldType = 'number' | 'integer' | 'string' | 'boolean' | 'time' | 'date' | 'enum' | 'json';

export interface FieldRule {
  field: string; type: FieldType; required: boolean;
  min?: number; minExclusive: boolean; max?: number; maxExclusive: boolean;
  scale?: number; maxLength?: number; pattern?: string; allowedValues?: string[];
}

export interface CrossFieldRule { code: string; fields: string[]; message: string }
export interface FormRuleSet { form: string; fields: FieldRule[]; crossFieldRules: CrossFieldRule[] }
export interface RuleInfo { code: string; kind: string; scope: string; message: string; source: string }

export interface ValidationRulesDocument {
  version: string; rules: RuleInfo[]; forms: FormRuleSet[]; fixedLimits: Record<string, string>;
}

export interface Violation { code: string; message: string; field?: string }

export type FormValues = Record<string, unknown>;

export const violationKey = (v: Violation): string => (v.field ? `${v.field}:${v.code}` : v.code);

export function findForm(doc: ValidationRulesDocument, form: string): FormRuleSet {
  const set = doc.forms.find((f) => f.form === form);
  if (!set) throw new Error(`Unknown form ${form}`);
  return set;
}

export function validateForm(doc: ValidationRulesDocument, form: string, values: FormValues): Violation[] {
  const set = findForm(doc, form);
  const out: Violation[] = [];
  for (const rule of set.fields) {
    const code = checkField(rule, values[rule.field]);
    if (code) out.push({ code, message: fieldMessage(code, rule), field: rule.field });
  }
  const bad = new Set(out.map((v) => v.field));
  for (const cross of set.crossFieldRules)
    if (!cross.fields.some((f) => bad.has(f)) && !crossHolds(cross.code, values, doc, set)) out.push({ code: cross.code, message: cross.message });
  return out;
}

export function checkField(rule: FieldRule, value: unknown): string | null {
  if (isEmpty(value)) return rule.required ? 'FIELD_REQUIRED' : null;
  switch (rule.type) {
    case 'number':
    case 'integer': {
      if (typeof value !== 'number' || !Number.isFinite(value)) return 'FIELD_TYPE';
      if (rule.type === 'integer' && !Number.isInteger(value)) return 'FIELD_TYPE';
      if (rule.min !== undefined && (rule.minExclusive ? value <= rule.min : value < rule.min)) return 'FIELD_MIN';
      if (rule.max !== undefined && (rule.maxExclusive ? value >= rule.max : value > rule.max)) return 'FIELD_MAX';
      if (rule.scale !== undefined && decimals(value) > rule.scale) return 'FIELD_SCALE';
      return null;
    }
    case 'boolean':
      return typeof value === 'boolean' ? null : 'FIELD_TYPE';
  }
  if (typeof value !== 'string') return 'FIELD_TYPE';
  switch (rule.type) {
    case 'string':
      if (rule.maxLength !== undefined && value.length > rule.maxLength) return 'FIELD_MAX_LENGTH';
      if (rule.pattern && !new RegExp(rule.pattern).test(value)) return 'FIELD_PATTERN';
      return null;
    case 'time':
      return parseTime(value) === null ? 'FIELD_TYPE' : null;
    case 'date':
      return isDate(value) ? null : 'FIELD_TYPE';
    case 'enum':
      return rule.allowedValues?.includes(value) ? null : 'FIELD_ENUM';
    case 'json':
      return isJsonObject(value) ? null : 'FIELD_TYPE';
  }
  return null;
}

function crossHolds(code: string, o: FormValues, doc: ValidationRulesDocument, set: FormRuleSet): boolean {
  const n = (f: string) => o[f] as number;
  const t = (f: string) => parseTime(o[f] as string) as number;
  const bool = (f: string) => o[f] === true;
  const text = (f: string) => (typeof o[f] === 'string' ? (o[f] as string) : null);
  const hasText = (f: string) => !!text(f);
  const marketOpen = parseTime(doc.fixedLimits['marketOpen']) as number;
  const lastMinute = (parseTime(doc.fixedLimits['marketCloseExclusive']) as number) - 60;
  const rank = (f: string) => set.fields.find((r) => r.field === f)?.allowedValues?.indexOf(text(f) ?? '') ?? -1;

  switch (code) {
    case 'CX_PREMIUM_BAND': return n('maxSingleTradePrice') > n('minSingleTradePrice');
    case 'CX_MAX_SL_GTE_DEFAULT_SL': return n('maxSlPct') >= n('defaultSlPct');
    case 'CX_FALLBACK_RR': return gte(n('defaultTargetPct'), n('defaultSlPct') * n('minRiskRewardRatio'));
    case 'CX_DAILY_LOSS_LTE_CAPITAL': return n('maxDailyLoss') <= n('allocatedCapital');
    case 'CX_DATA_PROVIDER_CREDENTIALS':
      return !(bool('isDataProvider') && bool('isActive'))
        || ((hasText('apiKey') || bool('hasApiKey')) && (hasText('apiSecret') || bool('hasApiSecret')));
    case 'CX_WEIGHTS_SUM_100': return Math.abs(n('patternWeightPct') + n('newsWeightPct') + n('oiGreeksWeightPct') - 100) < 1e-9;
    case 'CX_PREMARKET_BEFORE_OPEN': return t('premarketScanTime') < marketOpen;
    case 'CX_SESSION_TIMES':
      return t('entryStartTime') >= marketOpen && t('entryCutoffTime') > t('entryStartTime')
        && t('squareOffTime') > t('entryCutoffTime') && t('squareOffTime') <= lastMinute;
    case 'CX_AUTH_BEFORE_ENTRY': return t('authLoginTime') < t('entryStartTime');
    case 'CX_TREND_TF_ABOVE_TRIGGER': return rank('trendTimeframe') > rank('triggerTimeframe');
    case 'CX_REAL_CONFIRMATION': return text('targetMode') !== 'REAL' || text('confirmClientId')?.trim() === text('accountClientId');
    case 'CX_SPECIAL_SESSION_TIMES':
      return bool('isSpecialSession')
        ? !isEmpty(o['sessionOpenTime']) && !isEmpty(o['sessionCloseTime']) && t('sessionOpenTime') < t('sessionCloseTime')
        : isEmpty(o['sessionOpenTime']) && isEmpty(o['sessionCloseTime']);
  }
  throw new Error(`No implementation for cross-field rule ${code}`);
}

/** Fixed mandatory SL / target rules for a long option position (FX_* bracket codes). */
export function validateBracket(entry: number, stopLoss: number | null, target: number | null): Violation[] {
  const v: Violation[] = [];
  if (stopLoss === null) v.push({ code: 'FX_SL_REQUIRED', message: 'Stop-loss is mandatory.', field: 'stopLossPrice' });
  if (target === null) v.push({ code: 'FX_TARGET_REQUIRED', message: 'Target is mandatory.', field: 'targetSellPrice' });
  if (stopLoss !== null && stopLoss <= 0) v.push({ code: 'FX_SL_NOT_POSITIVE', message: 'Stop-loss must be positive.', field: 'stopLossPrice' });
  if (stopLoss !== null && stopLoss > 0 && stopLoss >= entry) v.push({ code: 'FX_SL_NOT_BELOW_ENTRY', message: 'Stop-loss must be below entry.', field: 'stopLossPrice' });
  if (target !== null && target <= entry) v.push({ code: 'FX_TARGET_NOT_ABOVE_ENTRY', message: 'Target must be above entry.', field: 'targetSellPrice' });
  return v;
}

export function fieldMessage(code: string, r: FieldRule): string {
  switch (code) {
    case 'FIELD_REQUIRED': return 'This field is required.';
    case 'FIELD_TYPE': return `Must be a valid ${r.type}.`;
    case 'FIELD_MIN': return `Must be ${r.minExclusive ? 'greater than' : 'at least'} ${r.min}.`;
    case 'FIELD_MAX': return `Must be ${r.maxExclusive ? 'less than' : 'at most'} ${r.max}.`;
    case 'FIELD_SCALE': return `At most ${r.scale} decimal places.`;
    case 'FIELD_MAX_LENGTH': return `At most ${r.maxLength} characters.`;
    case 'FIELD_PATTERN': return 'Invalid format.';
    case 'FIELD_ENUM': return `Must be one of: ${(r.allowedValues ?? []).join(', ')}.`;
  }
  return code;
}

export function isEmpty(v: unknown): boolean {
  return v === null || v === undefined || v === '';
}

/** Seconds since midnight for "HH:mm" or "HH:mm:ss", else null. */
export function parseTime(s: string): number | null {
  const m = /^([01]\d|2[0-3]):([0-5]\d)(?::([0-5]\d))?$/.exec(s);
  return m ? +m[1] * 3600 + +m[2] * 60 + (m[3] ? +m[3] : 0) : null;
}

function isDate(s: string): boolean {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s);
  if (!m) return false;
  const d = new Date(Date.UTC(+m[1], +m[2] - 1, +m[3]));
  return d.getUTCFullYear() === +m[1] && d.getUTCMonth() === +m[2] - 1 && d.getUTCDate() === +m[3];
}

function isJsonObject(s: string): boolean {
  try {
    const v: unknown = JSON.parse(s);
    return typeof v === 'object' && v !== null && !Array.isArray(v);
  } catch {
    return false;
  }
}

/** Decimal places of a JS number as typed (avoids binary float artefacts such as 0.29 * 100). */
export function decimals(n: number): number {
  const s = Math.abs(n).toString();
  const [mantissa, exp] = s.split('e');
  const frac = mantissa.split('.')[1]?.length ?? 0;
  return Math.max(0, frac - (exp ? +exp : 0));
}

function gte(a: number, b: number): boolean {
  return a >= b - 1e-9;
}
