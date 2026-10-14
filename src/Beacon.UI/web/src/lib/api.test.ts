import { describe, it, expect } from 'vitest';
import { ApiError, isNotFound, NOT_FOUND_DETAIL } from './api';

// The generated NSwag client throws its own ApiException; it is recognised by shape.
const generated = (status: number) => ({ isApiException: true, status, response: '', message: 'An unexpected server error occurred.' });

describe('isNotFound', () => {
  it('is true for a 404 from either client', () => {
    expect(isNotFound(new ApiError(404, ''))).toBe(true);
    expect(isNotFound(generated(404))).toBe(true);
  });

  it('is false for any other status, so a refused or failed request is not shown as missing', () => {
    expect(isNotFound(new ApiError(403, 'Forbidden'))).toBe(false);
    expect(isNotFound(new ApiError(500, ''))).toBe(false);
    expect(isNotFound(generated(409))).toBe(false);
  });

  it('is false for failures that never got an HTTP answer', () => {
    expect(isNotFound(new Error('Failed to fetch'))).toBe(false);
    expect(isNotFound(undefined)).toBe(false);
    expect(isNotFound({ status: 404 })).toBe(false);
  });
});

describe('NOT_FOUND_DETAIL', () => {
  it('covers both a missing resource and one the caller may not read, without saying which', () => {
    expect(NOT_FOUND_DETAIL).toMatch(/does not exist/i);
    expect(NOT_FOUND_DETAIL).toMatch(/do not have access/i);
  });
});
