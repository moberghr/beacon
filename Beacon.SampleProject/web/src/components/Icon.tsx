// Inline icon set — minimal stroke icons, original (Lucide-inspired).
// Vendored from Beacon-design/icons.jsx and ported to TypeScript.
import type { ReactNode } from 'react';

interface IconProps {
  size?: number;
  stroke?: number;
  fill?: string;
  className?: string;
  d?: string;
  children?: ReactNode;
}

const I = ({ d, children, size = 16, stroke = 1.6, fill = 'none', className = '' }: IconProps) => (
  <svg
    xmlns="http://www.w3.org/2000/svg"
    width={size}
    height={size}
    viewBox="0 0 24 24"
    fill={fill}
    stroke="currentColor"
    strokeWidth={stroke}
    strokeLinecap="round"
    strokeLinejoin="round"
    className={className}
  >
    {d ? <path d={d} /> : children}
  </svg>
);

type IconComp = (p: Omit<IconProps, 'd' | 'children'>) => JSX.Element;

export const Icon: Record<string, IconComp> = {
  Home: p => <I {...p}><path d="M3 11l9-7 9 7v9a2 2 0 0 1-2 2h-4v-6h-6v6H5a2 2 0 0 1-2-2z" /></I>,
  Tower: p => <I {...p}><circle cx="12" cy="12" r="3" /><path d="M5 12a7 7 0 0 1 7-7M19 12a7 7 0 0 0-7-7M12 21V15" /><path d="M9 21h6" /></I>,
  Grid: p => <I {...p}><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></I>,
  Database: p => <I {...p}><ellipse cx="12" cy="5" rx="8" ry="3" /><path d="M4 5v6c0 1.66 3.58 3 8 3s8-1.34 8-3V5" /><path d="M4 11v6c0 1.66 3.58 3 8 3s8-1.34 8-3v-6" /></I>,
  Folder: p => <I {...p}><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" /></I>,
  Shield: p => <I {...p}><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" /><path d="m9 12 2 2 4-4" /></I>,
  ArrowsLR: p => <I {...p}><path d="M7 7l-4 5 4 5M3 12h18M17 7l4 5-4 5" /></I>,
  Bot: p => <I {...p}><rect x="3" y="8" width="18" height="12" rx="2" /><path d="M12 8V4M8 4h8" /><circle cx="9" cy="14" r="1" fill="currentColor" /><circle cx="15" cy="14" r="1" fill="currentColor" /></I>,
  Query: p => <I {...p}><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /><path d="M8 11h6M11 8v6" /></I>,
  Bell: p => <I {...p}><path d="M6 8a6 6 0 0 1 12 0c0 7 3 7 3 9H3c0-2 3-2 3-9z" /><path d="M10 21a2 2 0 0 0 4 0" /></I>,
  Inbox: p => <I {...p}><path d="M22 12h-6l-2 3h-4l-2-3H2" /><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11Z" /></I>,
  Users: p => <I {...p}><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" /><circle cx="9" cy="7" r="4" /><path d="M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75" /></I>,
  Check: p => <I {...p}><circle cx="12" cy="12" r="9" /><path d="m8 12 3 3 5-6" /></I>,
  Book: p => <I {...p}><path d="M4 4h12a4 4 0 0 1 4 4v12H8a4 4 0 0 1-4-4z" /><path d="M4 16a4 4 0 0 1 4-4h12" /></I>,
  Key: p => <I {...p}><circle cx="7.5" cy="15.5" r="3.5" /><path d="m10 13 9-9 2 2-2 2 1 1-2 2-1-1-2 2" /></I>,
  Sliders: p => <I {...p}><path d="M4 6h16M4 12h16M4 18h16" /><circle cx="8" cy="6" r="1.8" fill="var(--surface)" /><circle cx="16" cy="12" r="1.8" fill="var(--surface)" /><circle cx="10" cy="18" r="1.8" fill="var(--surface)" /></I>,
  Wand: p => <I {...p}><path d="m4 20 9-9M14 5l1 2 2 1-2 1-1 2-1-2-2-1 2-1zM18 11l1 1 1-1-1-1zM18 17l1 1 1-1-1-1z" /></I>,
  Lightbulb: p => <I {...p}><path d="M9 18h6M10 22h4M12 2a6 6 0 0 0-3 11.2V16h6v-2.8A6 6 0 0 0 12 2z" /></I>,
  Cog: p => <I {...p}><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" /></I>,
  Info: p => <I {...p}><circle cx="12" cy="12" r="9" /><path d="M12 8v.01M11 12h1v4h1" /></I>,
  Refresh: p => <I {...p}><path d="M3 12a9 9 0 0 1 15-6.7L21 8" /><path d="M21 3v5h-5" /><path d="M21 12a9 9 0 0 1-15 6.7L3 16" /><path d="M3 21v-5h5" /></I>,
  Search: p => <I {...p}><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /></I>,
  Sun: p => <I {...p}><circle cx="12" cy="12" r="4" /><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41" /></I>,
  Moon: p => <I {...p}><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" /></I>,
  Plus: p => <I {...p}><path d="M12 5v14M5 12h14" /></I>,
  Download: p => <I {...p}><path d="M12 3v12M7 10l5 5 5-5M5 21h14" /></I>,
  Calendar: p => <I {...p}><rect x="3" y="5" width="18" height="16" rx="2" /><path d="M8 3v4M16 3v4M3 10h18" /></I>,
  Filter: p => <I {...p}><path d="M3 5h18l-7 8v6l-4-2v-4z" /></I>,
  Chevron: p => <I {...p}><path d="m9 6 6 6-6 6" /></I>,
  ChevronDown: p => <I {...p}><path d="m6 9 6 6 6-6" /></I>,
  Dots: p => <I {...p}><circle cx="5" cy="12" r="1.5" fill="currentColor" stroke="none" /><circle cx="12" cy="12" r="1.5" fill="currentColor" stroke="none" /><circle cx="19" cy="12" r="1.5" fill="currentColor" stroke="none" /></I>,
  Trend: p => <I {...p}><path d="M3 17l6-6 4 4 8-8" /><path d="M14 7h7v7" /></I>,
  Bolt: p => <I {...p}><path d="M13 2 4 14h7l-1 8 9-12h-7z" /></I>,
  Clock: p => <I {...p}><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></I>,
  Alert: p => <I {...p}><path d="M12 3 2 21h20z" /><path d="M12 10v5M12 18v.01" /></I>,
  Activity: p => <I {...p}><path d="M22 12h-4l-3 9-6-18-3 9H2" /></I>,
  Layers: p => <I {...p}><path d="m12 3 9 5-9 5-9-5z" /><path d="m3 13 9 5 9-5M3 18l9 5 9-5" /></I>,
  Branch: p => <I {...p}><circle cx="6" cy="6" r="2.5" /><circle cx="18" cy="6" r="2.5" /><circle cx="12" cy="18" r="2.5" /><path d="M6 8.5v.5a4 4 0 0 0 4 4h4a4 4 0 0 0 4-4v-.5" /><path d="M12 13v2.5" /></I>,
  Plug: p => <I {...p}><path d="M9 2v6M15 2v6M7 8h10v3a5 5 0 0 1-10 0z" /><path d="M12 16v6" /></I>,
  Box: p => <I {...p}><path d="m3 7 9-4 9 4-9 4z" /><path d="M3 7v10l9 4 9-4V7M12 11v10" /></I>,
  Spark: p => <I {...p}><path d="m12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5z" /></I>,
  ArrowUp: p => <I {...p}><path d="M12 19V5M5 12l7-7 7 7" /></I>,
  ArrowDown: p => <I {...p}><path d="M12 5v14M19 12l-7 7-7-7" /></I>,
  Lock: p => <I {...p}><rect x="4" y="11" width="16" height="10" rx="2" /><path d="M8 11V8a4 4 0 0 1 8 0v3" /></I>,
  X: p => <I {...p}><path d="M6 6l12 12M18 6 6 18" /></I>,
};
