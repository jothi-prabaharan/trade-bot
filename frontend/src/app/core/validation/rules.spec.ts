import vectorsJson from '../../../../../shared/validation/validation-vectors.json';
import rulesJson from '../../../../../shared/validation/rules-document.json';
import { FormValues, ValidationRulesDocument, decimals, parseTime, validateBracket, validateForm, violationKey } from './rules';

interface FormCase { name: string; patch: FormValues; expect: string[] }
interface BracketCase { name: string; entry: number; stopLoss: number | null; target: number | null; expect: string[] }
interface Vectors { bracket: BracketCase[]; forms: Record<string, { base: FormValues; cases: FormCase[] }> }

const vectors = vectorsJson as unknown as Vectors;
const doc = rulesJson as unknown as ValidationRulesDocument;
const sorted = (xs: string[]) => [...xs].sort();

describe('shared validation vectors (same file as the C# suite)', () => {
  for (const [form, spec] of Object.entries(vectors.forms)) {
    describe(form, () => {
      for (const c of spec.cases) {
        it(c.name, () => {
          const values = { ...structuredClone(spec.base), ...structuredClone(c.patch) };
          expect(sorted(validateForm(doc, form, values).map(violationKey))).toEqual(sorted(c.expect));
        });
      }
    });
  }

  describe('bracket', () => {
    for (const c of vectors.bracket) {
      it(c.name, () => {
        expect(sorted(validateBracket(c.entry, c.stopLoss, c.target).map(violationKey))).toEqual(sorted(c.expect));
      });
    }
  });

  it('covers every form served by the API and every cross-field rule', () => {
    expect(Object.keys(vectors.forms).sort()).toEqual(doc.forms.map((f) => f.form).sort());
    const expected = new Set(Object.values(vectors.forms).flatMap((f) => f.cases.flatMap((c) => c.expect)));
    for (const code of doc.forms.flatMap((f) => f.crossFieldRules.map((r) => r.code))) expect(expected.has(code)).toBe(true);
  });
});

describe('number and time helpers', () => {
  it('counts typed decimal places without float artefacts', () => {
    expect(decimals(0.29)).toBe(2);
    expect(decimals(100)).toBe(0);
    expect(decimals(1e-7)).toBe(7);
    expect(decimals(12.3456)).toBe(4);
  });

  it('parses HH:mm and HH:mm:ss only', () => {
    expect(parseTime('09:15')).toBe(33300);
    expect(parseTime('15:29:59')).toBe(55799);
    expect(parseTime('24:00')).toBeNull();
    expect(parseTime('9:15')).toBeNull();
  });
});
