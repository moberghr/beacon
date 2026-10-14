/**
 * Characters a review screen does not show, which Beacon refuses in grounding text (a golden case's question and SQL, a
 * learned pattern's example question and SQL) and leaves out of prompts: format characters (Unicode category Cf —
 * bidirectional overrides and isolates, zero-width characters, the byte order mark), the tag characters U+E0000–U+E007F
 * and the variation selectors (U+FE00–U+FE0F, U+E0100–U+E01EF, U+180B–U+180F). The same set as the server's check.
 */
const HIDDEN = /[\p{Cf}\u{E0000}-\u{E007F}\u{FE00}-\u{FE0F}\u{E0100}-\u{E01EF}\u{180B}-\u{180F}]/u;
const HIDDEN_ALL = /[\p{Cf}\u{E0000}-\u{E007F}\u{FE00}-\u{FE0F}\u{E0100}-\u{E01EF}\u{180B}-\u{180F}]/gu;

/** True when `text` holds a character a reviewer cannot see. */
export function hasHiddenCharacters(text: string | null | undefined): boolean {
  return !!text && HIDDEN.test(text);
}

/** `text` with every hidden character shown as a visible `\u{XXXX}` token, so a reviewer sees what a prompt would get. */
export function showHiddenCharacters(text: string): string {
  return text.replace(HIDDEN_ALL, x => `\\u{${(x.codePointAt(0) ?? 0).toString(16).toUpperCase().padStart(4, '0')}}`);
}
