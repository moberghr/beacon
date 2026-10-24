import { describe, expect, it } from 'vitest';
import { rowsToTsv } from './ResultsTable';

describe('rowsToTsv', () => {
  it('keeps numbers and ordinary text as they are', () => {
    expect(rowsToTsv([{ a: -5, b: 'hello-world', c: 'x=1' }])).toBe('a\tb\tc\n-5\thello-world\tx=1');
  });

  it('writes a text value that starts like a formula as text', () => {
    const tsv = rowsToTsv([{ '=h': '=1+1', b: '+1', c: '-1', d: '@SUM(A1)' }]);
    expect(tsv).toBe("'=h\tb\tc\td\n'=1+1\t'+1\t'-1\t'@SUM(A1)");
  });

  it('still removes tabs and line breaks inside a value', () => {
    expect(rowsToTsv([{ a: 'x\ty\nz' }])).toBe('a\nx y z');
  });
});
