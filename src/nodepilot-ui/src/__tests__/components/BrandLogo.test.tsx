import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { BrandLogo } from '../../components/BrandLogo';
import { useThemeStore } from '../../stores/themeStore';

describe('BrandLogo', () => {
  beforeEach(() => {
    useThemeStore.setState({ theme: 'light', resolvedTheme: 'light' });
  });

  it.each([
    ['light', '/appicon-light.png'],
    ['dark', '/appicon-dark.png'],
    ['dark-lila', '/appicon-dark-lila.png'],
    ['light-grey', '/appicon-light-grey.png'],
    ['dark-sparkasse', '/appicon-dark-sparkasse.png'],
    ['light-sparkasse', '/appicon-light-sparkasse.png'],
    ['dark-nebula', '/appicon-dark-nebula.png'],
  ])('renders the %s skin variant', (skin, expected) => {
    useThemeStore.setState({ theme: skin as never });
    render(<BrandLogo />);
    expect(screen.getByRole('img')).toHaveAttribute('src', expected);
  });

  it('resolves the system theme to the resolved light/dark variant', () => {
    useThemeStore.setState({ theme: 'system', resolvedTheme: 'dark' });
    render(<BrandLogo />);
    expect(screen.getByRole('img')).toHaveAttribute('src', '/appicon-dark.png');
  });

  it('keeps object-contain and forwards className + alt', () => {
    render(<BrandLogo className="w-9 h-9 shrink-0" alt="NodePilot logo" />);
    const img = screen.getByRole('img', { name: 'NodePilot logo' });
    expect(img.className).toContain('object-contain');
    expect(img.className).toContain('w-9');
  });
});
