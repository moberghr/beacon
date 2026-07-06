import { ParameterType } from '@/lib/enums';

/**
 * Shape every page consumes when working with a query parameter. Both
 * `NewQueryPage` and `QueryEditorPage` carry the same four fields — they
 * just call the local type something different. Keep this shape minimal
 * and extend at the call site if the page needs more.
 */
export interface DetectedParameter {
  name: string;
  type: ParameterType;
  description: string | null;
  placeholder: string | null;
  /**
   * True when the parameter was added explicitly via the "add parameter"
   * dialog rather than detected from a `{name}` placeholder. Manual entries
   * survive rescans even while their placeholder isn't (yet) in the SQL.
   */
  isManual?: boolean;
}

/**
 * `{paramName}` markers in the SQL drive the parameter list. The regex is
 * intentionally simple — same one Blazor used pre-cutover so behavior
 * stays identical. Word-character names only; no nesting; no escaping.
 */
const PARAM_REGEX = /\{(\w+)\}/g;

/**
 * Scan `sql` for `{param}` placeholders and reconcile against the existing
 * parameter list. Returns a fresh array preserving the metadata
 * (`type`/`description`/`placeholder`) of any existing entry, in the order
 * the placeholders first appear in the SQL. New placeholders default to
 * String with `{name}` as the placeholder text.
 *
 * Manually added parameters (`isManual: true`) whose placeholder isn't in
 * the SQL yet are preserved and appended after the detected ones — only
 * previously-detected parameters are dropped when their placeholder goes.
 *
 * Pure — call freely from React state setters.
 */
export function detectParameters<P extends DetectedParameter = DetectedParameter>(
  sql: string,
  existing: readonly P[],
): P[] {
  const seen = new Set<string>();
  const detected: string[] = [];
  PARAM_REGEX.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = PARAM_REGEX.exec(sql)) != null) {
    const name = match[1];
    if (!seen.has(name)) {
      seen.add(name);
      detected.push(name);
    }
  }

  const byName = new Map(existing.map((p) => [p.name, p]));
  const result = detected.map((name) =>
    (byName.get(name) ?? ({
      name,
      type: ParameterType.String,
      description: null,
      placeholder: `{${name}}`,
    } as P)),
  );
  const manualUnreferenced = existing.filter((p) => p.isManual && !seen.has(p.name));
  return [...result, ...manualUnreferenced];
}
