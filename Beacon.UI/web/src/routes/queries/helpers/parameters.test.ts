import { describe, expect, it } from 'vitest';
import { detectParameters, type DetectedParameter } from './parameters';
import { ParameterType } from '@/lib/enums';

function p(name: string, overrides: Partial<DetectedParameter> = {}): DetectedParameter {
  return {
    name,
    type: ParameterType.String,
    description: null,
    placeholder: `{${name}}`,
    ...overrides,
  };
}

describe('detectParameters', () => {
  it('returns an empty list for SQL without placeholders', () => {
    expect(detectParameters<DetectedParameter>('select 1', [])).toEqual([]);
  });

  it('extracts one entry per unique placeholder, preserving first-seen order', () => {
    const out = detectParameters<DetectedParameter>('select * from t where a = {x} and b = {y} and c = {x}', []);
    expect(out.map((q) => q.name)).toEqual(['x', 'y']);
  });

  it('preserves existing metadata for placeholders that survive the rescan', () => {
    const existing = [p('x', { type: ParameterType.Number, description: 'an int' })];
    const out = detectParameters<DetectedParameter>('select {x}, {y}', existing);
    expect(out[0]).toEqual(existing[0]);
    expect(out[1]).toEqual(p('y'));
  });

  it('drops previously detected parameters that no longer appear in the SQL', () => {
    const existing = [p('removed'), p('kept')];
    const out = detectParameters<DetectedParameter>('select {kept}', existing);
    expect(out.map((q) => q.name)).toEqual(['kept']);
  });

  it('preserves manually added parameters whose placeholder is not (yet) in the SQL', () => {
    const existing = [p('manual', { isManual: true }), p('detected')];
    const out = detectParameters<DetectedParameter>('select {detected}', existing);
    expect(out.map((q) => q.name)).toEqual(['detected', 'manual']);
    expect(out[1]).toEqual(existing[0]);
  });

  it('keeps a manual parameter once its placeholder appears, without duplicating it', () => {
    const existing = [p('manual', { isManual: true, type: ParameterType.Number })];
    const out = detectParameters<DetectedParameter>('select {manual}', existing);
    expect(out).toEqual([existing[0]]);
  });

  it('defaults new placeholders to String type with `{name}` as the placeholder text', () => {
    const out = detectParameters<DetectedParameter>('select {z}', []);
    expect(out[0]).toEqual(p('z'));
  });
});
