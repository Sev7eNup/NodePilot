import { describe, it, expect, afterEach, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { AppLayout } from '../../../components/layout/AppLayout';
import { useAuthStore } from '../../../stores/authStore';

const originalMatchMedia = window.matchMedia;

/** Install a static matchMedia mock so `useIsMobile()` resolves deterministically. */
function setViewport(mobile: boolean) {
  window.matchMedia = ((q: string) => ({
    matches: mobile, media: q, onchange: null,
    addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}

function renderShell() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route element={<AppLayout />}>
            <Route index element={<div>Home Page</div>} />
            <Route path="workflows" element={<div>Workflows Page</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function getAside(container: HTMLElement): HTMLElement {
  const aside = container.querySelector('aside');
  if (!aside) throw new Error('sidebar <aside> not found');
  return aside as HTMLElement;
}

beforeEach(() => {
  // TopBar polls /healthz/live and /api/system host-info; keep them quiet.
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));
  useAuthStore.setState({ userId: 'u1', username: 'admin', role: 'Admin', isAuthenticated: true });
});

afterEach(() => {
  window.matchMedia = originalMatchMedia;
  vi.restoreAllMocks();
  useAuthStore.setState({ userId: null, username: null, role: null, isAuthenticated: null });
});

describe('AppLayout — mobile drawer', () => {
  it('renders the sidebar as a closed off-canvas drawer with a hamburger trigger', () => {
    setViewport(true);
    const { container } = renderShell();
    expect(screen.getByLabelText('Open menu')).toBeInTheDocument();
    const aside = getAside(container);
    expect(aside.className).toContain('fixed');
    expect(aside.className).toContain('-translate-x-full');
  });

  it('opens the drawer (with backdrop) when the hamburger is clicked', () => {
    setViewport(true);
    const { container } = renderShell();
    // Closed: only the in-sidebar close button carries the "Close menu" label.
    expect(screen.getAllByLabelText('Close menu')).toHaveLength(1);

    fireEvent.click(screen.getByLabelText('Open menu'));

    const aside = getAside(container);
    expect(aside.className).toContain('translate-x-0');
    expect(aside.className).not.toContain('-translate-x-full');
    // Open: backdrop + in-sidebar close button.
    expect(screen.getAllByLabelText('Close menu')).toHaveLength(2);
  });

  it('closes the drawer when the backdrop is clicked', () => {
    setViewport(true);
    const { container } = renderShell();
    fireEvent.click(screen.getByLabelText('Open menu'));
    // Backdrop is the first "Close menu" element (rendered before the sidebar in the tree).
    fireEvent.click(screen.getAllByLabelText('Close menu')[0]);
    expect(getAside(container).className).toContain('-translate-x-full');
  });

  it('navigates and auto-closes the drawer when a nav link is tapped', () => {
    setViewport(true);
    const { container } = renderShell();
    fireEvent.click(screen.getByLabelText('Open menu'));
    const aside = getAside(container);
    fireEvent.click(within(aside).getByRole('link', { name: 'Workflows' }));
    expect(screen.getByText('Workflows Page')).toBeInTheDocument();
    expect(getAside(container).className).toContain('-translate-x-full');
  });

  it('keeps the nav scrollable and pads the bottom action area past the OS nav bar', () => {
    setViewport(true);
    const { container } = renderShell();
    fireEvent.click(screen.getByLabelText('Open menu'));
    const aside = getAside(container);
    // Nav scrolls so a short viewport never pushes the bottom area off-screen.
    const nav = aside.querySelector('nav');
    expect(nav?.className).toContain('overflow-y-auto');
    expect(nav?.className).toContain('min-h-0');
    // Bottom area (carries the logout action) clears the phone's system nav bar.
    const logout = within(aside).getByRole('button', { name: 'Logout' });
    const bottomArea = logout.closest('div.border-t');
    expect(bottomArea?.className).toContain('env(safe-area-inset-bottom)');
  });
});

describe('AppLayout — desktop', () => {
  it('renders a static in-flow sidebar without drawer transforms', () => {
    setViewport(false);
    const { container } = renderShell();
    const aside = getAside(container);
    expect(aside.className).toContain('np-sidebar-expanded');
    expect(aside.className).toContain('shrink-0');
    expect(aside.className).not.toContain('fixed');
    expect(aside.className).not.toContain('-translate-x-full');
  });
});
