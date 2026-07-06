import type { Config } from 'tailwindcss';

/**
 * Beacon — Tailwind config.
 *
 * Colors reference CSS variables defined in `src/index.css` so light/dark
 * mode + runtime tweaks keep working. Switch theme via
 *   document.documentElement.dataset.theme = 'dark'.
 */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx,js,jsx}'],
  darkMode: ['selector', '[data-theme="dark"]'],
  theme: {
    extend: {
      colors: {
        brand: {
          50: 'var(--brand-50)',
          100: 'var(--brand-100)',
          200: 'var(--brand-200)',
          300: 'var(--brand-300)',
          400: 'var(--brand-400)',
          500: 'var(--brand-500)',
          600: 'var(--brand-600)',
          700: 'var(--brand-700)',
          800: 'var(--brand-800)',
          900: 'var(--brand-900)',
          DEFAULT: 'var(--brand-500)',
        },
        bg: 'var(--bg)',
        surface: 'var(--surface)',
        'surface-2': 'var(--surface-2)',
        border: 'var(--border)',
        'border-strong': 'var(--border-strong)',
        text: 'var(--text)',
        'text-muted': 'var(--text-muted)',
        'text-subtle': 'var(--text-subtle)',
        ok: 'var(--ok)',
        warn: 'var(--warn)',
        crit: 'var(--crit)',
        info: 'var(--info)',
        'ok-bg': 'var(--ok-bg)',
        'warn-bg': 'var(--warn-bg)',
        'crit-bg': 'var(--crit-bg)',
        'info-bg': 'var(--info-bg)',
      },
      fontFamily: {
        sans: ['Geist', 'ui-sans-serif', 'system-ui', '-apple-system', 'Segoe UI', 'sans-serif'],
        mono: ['Geist Mono', 'ui-monospace', 'SF Mono', 'Menlo', 'monospace'],
      },
      fontSize: {
        '2xs': ['10px', { lineHeight: '1.3' }],
        xs: ['11px', { lineHeight: '1.4' }],
        sm: ['12.5px', { lineHeight: '1.5' }],
        base: ['14px', { lineHeight: '1.5' }],
        lg: ['16px', { lineHeight: '1.5' }],
      },
      borderRadius: {
        xs: '4px',
        sm: '6px',
        md: '8px',
        lg: '12px',
        xl: '16px',
      },
      boxShadow: {
        sm: '0 1px 2px oklch(20% 0.02 240 / 0.04), 0 1px 1px oklch(20% 0.02 240 / 0.03)',
        md: '0 2px 4px oklch(20% 0.02 240 / 0.04), 0 4px 12px oklch(20% 0.02 240 / 0.05)',
        pop: '0 8px 24px oklch(20% 0.02 240 / 0.08), 0 2px 4px oklch(20% 0.02 240 / 0.04)',
        ring: '0 0 0 3px oklch(58% 0.095 175 / 0.20)',
      },
      letterSpacing: {
        tightish: '-0.01em',
        tighter: '-0.025em',
        eyebrow: '0.07em',
        label: '0.08em',
      },
      keyframes: {
        'beacon-pulse': {
          '0%, 100%': { opacity: '1', transform: 'scale(1)' },
          '50%': { opacity: '0.6', transform: 'scale(0.85)' },
        },
        'beacon-sweep': {
          '0%': { transform: 'rotate(-22deg)', opacity: '0' },
          '10%': { opacity: '0.55' },
          '50%': { transform: 'rotate(22deg)', opacity: '0.55' },
          '60%': { opacity: '0' },
          '100%': { transform: 'rotate(-22deg)', opacity: '0' },
        },
        'beacon-rings': {
          '0%': { transform: 'scale(0.6)', opacity: '0' },
          '20%': { opacity: '1' },
          '100%': { transform: 'scale(1.4)', opacity: '0' },
        },
        'beacon-draw': {
          to: { strokeDashoffset: '0' },
        },
      },
      animation: {
        'beacon-pulse': 'beacon-pulse 2.4s ease-in-out infinite',
        'beacon-sweep': 'beacon-sweep 6.5s cubic-bezier(.5,.05,.5,.95) infinite',
        'beacon-rings': 'beacon-rings 4s ease-out infinite',
        'beacon-draw': 'beacon-draw 1.4s 0.4s ease-out forwards',
      },
    },
  },
  plugins: [],
} satisfies Config;
