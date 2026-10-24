import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { AuthLayout } from './AuthLayout';

describe('AuthLayout', () => {
  it('does not show the build identity before sign-in', () => {
    const { container } = render(
      <AuthLayout eyebrow="Sign in" title="Welcome" subtitle="Sign in">
        <div>form</div>
      </AuthLayout>,
    );

    expect(screen.getByText('form')).toBeInTheDocument();
    expect(container.querySelector('[title^="Build "]')).toBeNull();
    expect(screen.queryByText('dev')).not.toBeInTheDocument();
  });
});
