import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProviders } from '@/test/render';
import { RestrictedPill } from '@/routes/projects/RestrictedPill';
import { ProjectChips } from './ProjectChips';

describe('ProjectChips', () => {
  it('marks a source in no project as Admin-only when it is not open while unlinked', () => {
    renderWithProviders(<ProjectChips projects={[]} otherProjectCount={0} openWhenUnlinked={false} />);

    expect(screen.getByText('Admins only')).toHaveAttribute('title', expect.stringMatching(/only admins can use it/i));
    expect(screen.queryByText('Not linked')).not.toBeInTheDocument();
  });

  it('shows an unlinked source that stays open as just not linked', () => {
    renderWithProviders(<ProjectChips projects={[]} otherProjectCount={0} openWhenUnlinked />);

    expect(screen.getByText('Not linked')).toBeInTheDocument();
    expect(screen.queryByText('Admins only')).not.toBeInTheDocument();
  });

  it('names the readable projects, counts the others, and ignores the unlinked state while linked', () => {
    renderWithProviders(
      <ProjectChips projects={[{ id: 5, name: 'Ledger' }]} otherProjectCount={3} openWhenUnlinked={false} />,
    );

    expect(screen.getByText('Ledger')).toBeInTheDocument();
    expect(screen.getByText('+3 other')).toHaveAttribute('title', 'Projects you do not have access to');
    expect(screen.queryByText('Admins only')).not.toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('links each readable project to its page on detail views', () => {
    renderWithProviders(
      <ProjectChips projects={[{ id: 5, name: 'Ledger' }, { id: 6, name: 'Audit' }]} otherProjectCount={0} openWhenUnlinked linked />,
    );

    expect(screen.getByRole('link', { name: 'Ledger' })).toHaveAttribute('href', '/projects/5');
    expect(screen.getByRole('link', { name: 'Audit' })).toHaveAttribute('href', '/projects/6');
    expect(screen.queryByText(/other$/)).not.toBeInTheDocument();
  });
});

describe('RestrictedPill', () => {
  it('says the project is for its members and Admins only', () => {
    renderWithProviders(<RestrictedPill />);

    expect(screen.getByText('Restricted')).toHaveAttribute('title', expect.stringMatching(/only members and admins/i));
  });
});
