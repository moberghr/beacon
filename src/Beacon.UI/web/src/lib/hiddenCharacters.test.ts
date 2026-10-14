import { describe, expect, it } from 'vitest';
import { hasHiddenCharacters, showHiddenCharacters } from './hiddenCharacters';

describe('hidden characters', () => {
  it.each([
    ['a‮b', 'a\\u{202E}b'],
    ['a⁦b⁩', 'a\\u{2066}b\\u{2069}'],
    ['id​', 'id\\u{200B}'],
    ['﻿SELECT 1', '\\u{FEFF}SELECT 1'],
    ['a\u{E0041}b', 'a\\u{E0041}b'],
    ['a️b', 'a\\u{FE0F}b'],
    ['a\u{E0100}b', 'a\\u{E0100}b'],
    ['a᠋b', 'a\\u{180B}b'],
  ])('shows %j escaped', (text, shown) => {
    expect(hasHiddenCharacters(text)).toBe(true);
    expect(showHiddenCharacters(text)).toBe(shown);
  });

  it('leaves visible text, line breaks and non-Latin letters as they are', () => {
    const text = "SELECT naziv FROM kupci WHERE grad = 'Zagreb'\n-- čćžšđ";

    expect(hasHiddenCharacters(text)).toBe(false);
    expect(showHiddenCharacters(text)).toBe(text);
  });

  it('answers the same on every call', () => {
    expect(hasHiddenCharacters('a‮b')).toBe(true);
    expect(hasHiddenCharacters('a‮b')).toBe(true);
    expect(hasHiddenCharacters(null)).toBe(false);
    expect(hasHiddenCharacters('')).toBe(false);
  });
});
