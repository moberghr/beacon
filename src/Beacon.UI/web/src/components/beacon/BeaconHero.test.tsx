import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { BeaconHero } from './BeaconHero';

describe('BeaconHero status strip', () => {
  const ticks: Array<'ok' | 'warn' | 'crit' | 'muted'> = ['ok', 'warn', 'crit', 'muted'];

  it('reads its ticks generically and shows no legend unless the page gives one', () => {
    render(<BeaconHero ticks={ticks} meta={{ executions30d: 0, anomalies: 0, avgMs: 0 }} />);

    expect(screen.getByTitle('3h ago: ok')).toBeInTheDocument();
    expect(screen.getByTitle('2h ago: warnings')).toBeInTheDocument();
    expect(screen.getByTitle('1h ago: failures')).toBeInTheDocument();
    expect(screen.getByTitle('this hour: no executions')).toBeInTheDocument();
    expect(screen.queryByText('skipped')).not.toBeInTheDocument();
  });

  it("explains each tick colour in the page's words, a skipped run included", () => {
    render(
      <BeaconHero
        ticks={ticks}
        tickTitles={{ warn: 'runs skipped' }}
        legend={[
          { tick: 'ok', label: 'ok' },
          { tick: 'warn', label: 'skipped' },
          { tick: 'crit', label: 'failed' },
        ]}
        meta={{ executions30d: 0, anomalies: 0, avgMs: 0 }}
      />,
    );

    expect(screen.getByTitle('2h ago: runs skipped')).toBeInTheDocument();
    expect(screen.getByTitle('1h ago: failures')).toBeInTheDocument();
    expect(screen.getByText('skipped')).toBeInTheDocument();
    expect(screen.getByText('failed')).toBeInTheDocument();
  });
});
