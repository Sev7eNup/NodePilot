import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within, fireEvent, waitForElementToBeRemoved } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactElement } from 'react';
import { FolderPathBreadcrumb } from '../../components/designer/FolderPathBreadcrumb';
import { sharedFoldersApi, ROOT_FOLDER_ID, type SharedFolder } from '../../api/sharedFolders';
import { api } from '../../api/client';
import type { Workflow } from '../../types/api';

/**
 * FolderPathBreadcrumb renders the open workflow's folder path as a top-left canvas overlay.
 * Display is derived from `workflow.folderPath`; each segment is resolved against the
 * RBAC-visible `['shared-folders']` list and only resolved segments are interactive. The
 * per-segment popover lists that folder's sub-folders + workflows (from `['workflows']`,
 * with the current workflow merged in case the recent-500 cap excluded it) and lets the user
 * drill in and open workflows. We pin: chain rendering, hover/click open, drill-in, navigation
 * callback, RBAC-hidden ancestor → non-interactive, current-workflow merge, and Escape /
 * outside-pointerdown close.
 */

vi.mock('../../api/sharedFolders', async () => {
  const actual = await vi.importActual<typeof import('../../api/sharedFolders')>('../../api/sharedFolders');
  return { ...actual, sharedFoldersApi: { list: vi.fn() } };
});
vi.mock('../../api/client', () => ({ api: { get: vi.fn() } }));

const listMock = vi.mocked(sharedFoldersApi.list);
const getMock = vi.mocked(api.get);

function makeFolder(over: Partial<SharedFolder>): SharedFolder {
  return {
    id: 'folder-id', parentFolderId: null, name: 'Folder', path: '/Folder', depth: 1,
    createdAt: '2024-01-01T00:00:00Z', createdByUserId: null, workflowCount: 0,
    capabilities: { canRead: true, canRun: true, canEdit: false, canAdmin: false },
    ...over,
  };
}

function makeWorkflow(over: Partial<Workflow>): Workflow {
  return {
    id: 'wf', name: 'WF', description: null, definitionJson: '{}', version: 1, isEnabled: true,
    createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z', createdBy: null, updatedBy: null,
    ...over,
  };
}

const ROOT = makeFolder({ id: ROOT_FOLDER_ID, parentFolderId: null, name: 'Root', path: '/', depth: 0 });
const FINANCE = makeFolder({ id: 'finance', parentFolderId: ROOT_FOLDER_ID, name: 'Finance', path: '/Finance', depth: 1, workflowCount: 1 });
const REPORTS = makeFolder({ id: 'reports', parentFolderId: 'finance', name: 'Reports', path: '/Finance/Reports', depth: 2, workflowCount: 2 });

const WF_CURRENT = makeWorkflow({ id: 'wf1', name: 'Quarterly Report', folderId: 'reports', folderPath: '/Finance/Reports' });
const WF_SIBLING = makeWorkflow({ id: 'wf2', name: 'Annual Report', folderId: 'reports', folderPath: '/Finance/Reports' });
const WF_FINANCE = makeWorkflow({ id: 'wf3', name: 'Budget Plan', folderId: 'finance', folderPath: '/Finance' });

function renderBreadcrumb(
  workflow: Workflow,
  onOpenWorkflow: (w: Workflow) => void = vi.fn(),
): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const ui: ReactElement = (
    <QueryClientProvider client={client}>
      <FolderPathBreadcrumb workflow={workflow} currentWorkflowId={workflow.id} onOpenWorkflow={onOpenWorkflow} />
    </QueryClientProvider>
  );
  return render(ui);
}

describe('FolderPathBreadcrumb', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    listMock.mockResolvedValue([ROOT, FINANCE, REPORTS]);
    getMock.mockResolvedValue([WF_CURRENT, WF_SIBLING, WF_FINANCE]);
  });

  it('renders the named folder segments and no clickable Root', async () => {
    renderBreadcrumb(WF_CURRENT);

    expect(await screen.findByRole('button', { name: /Finance/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Reports/ })).toBeInTheDocument();
    // Root is a static home indicator, never a clickable/browsable segment.
    expect(screen.queryByRole('button', { name: 'Root' })).toBeNull();
  });

  it('renders nothing for a root-level workflow (empty folderPath)', async () => {
    const rootWf = makeWorkflow({ id: 'wf0', name: 'At Root', folderId: ROOT_FOLDER_ID, folderPath: '/' });
    const { container } = renderBreadcrumb(rootWf);
    // No segments → component returns null.
    expect(container.querySelector('[data-testid="folder-path-breadcrumb"]')).toBeNull();
  });

  it('opens a segment popover on hover (after the open delay)', async () => {
    renderBreadcrumb(WF_CURRENT);
    const reports = await screen.findByRole('button', { name: /Reports/ });

    expect(screen.queryByTestId('folder-contents-browser')).toBeNull();
    await userEvent.hover(reports);
    expect(screen.queryByTestId('folder-contents-browser')).toBeNull(); // not immediate
    expect(await screen.findByTestId('folder-contents-browser')).toBeInTheDocument();
  });

  it('lists the folder’s sub-folders and workflows', async () => {
    renderBreadcrumb(WF_CURRENT);
    await userEvent.click(await screen.findByRole('button', { name: /Finance/ }));

    const popover = await screen.findByTestId('folder-contents-browser');
    // Finance contains sub-folder Reports + workflow Budget Plan.
    expect(within(popover).getByText('Reports')).toBeInTheDocument();
    expect(within(popover).getByText('Budget Plan')).toBeInTheDocument();
    // Workflows from other folders are not listed.
    expect(within(popover).queryByText('Annual Report')).toBeNull();
  });

  it('drills into a sub-folder without navigating', async () => {
    const onOpen = vi.fn();
    renderBreadcrumb(WF_CURRENT, onOpen);
    await userEvent.click(await screen.findByRole('button', { name: /Finance/ }));

    const popover = await screen.findByTestId('folder-contents-browser');
    await userEvent.click(within(popover).getByText('Reports')); // sub-folder row

    const drilled = screen.getByTestId('folder-contents-browser');
    expect(within(drilled).getByText('Quarterly Report')).toBeInTheDocument();
    expect(within(drilled).getByText('Annual Report')).toBeInTheDocument();
    expect(onOpen).not.toHaveBeenCalled();
  });

  it('calls onOpenWorkflow when a workflow row is clicked', async () => {
    const onOpen = vi.fn();
    renderBreadcrumb(WF_CURRENT, onOpen);
    await userEvent.click(await screen.findByRole('button', { name: /Reports/ }));

    const popover = await screen.findByTestId('folder-contents-browser');
    await userEvent.click(within(popover).getByText('Annual Report'));

    expect(onOpen).toHaveBeenCalledTimes(1);
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ id: 'wf2' }));
  });

  it('renders RBAC-hidden ancestors as non-interactive text', async () => {
    // Finance is not visible to this caller; only the leaf folder is.
    listMock.mockResolvedValue([REPORTS]);
    getMock.mockResolvedValue([WF_CURRENT, WF_SIBLING]);
    renderBreadcrumb(WF_CURRENT);

    // Reports resolves → interactive button.
    expect(await screen.findByRole('button', { name: /Reports/ })).toBeInTheDocument();
    // Finance does not resolve → plain, non-interactive span.
    const finance = screen.getByText('Finance');
    expect(finance.tagName).toBe('SPAN');
    expect(finance.closest('button')).toBeNull();

    await userEvent.hover(finance);
    await new Promise((r) => setTimeout(r, 300));
    expect(screen.queryByTestId('folder-contents-browser')).toBeNull();
  });

  it('shows the current workflow in its folder even when the recent-500 list omits it', async () => {
    // Global list excludes the currently open workflow (wf1).
    getMock.mockResolvedValue([WF_SIBLING, WF_FINANCE]);
    renderBreadcrumb(WF_CURRENT);

    await userEvent.click(await screen.findByRole('button', { name: /Reports/ }));
    const popover = await screen.findByTestId('folder-contents-browser');
    expect(within(popover).getByText('Quarterly Report')).toBeInTheDocument(); // merged in
  });

  it('keeps the popover open while the pointer moves from the segment into it', async () => {
    renderBreadcrumb(WF_CURRENT);
    const reports = await screen.findByRole('button', { name: /Reports/ });

    await userEvent.hover(reports);
    const popover = await screen.findByTestId('folder-contents-browser');

    await userEvent.unhover(reports);
    await userEvent.hover(popover); // cancels the close timer
    await new Promise((r) => setTimeout(r, 200));
    expect(screen.getByTestId('folder-contents-browser')).toBeInTheDocument();

    await userEvent.unhover(popover);
    await waitForElementToBeRemoved(() => screen.queryByTestId('folder-contents-browser'));
  });

  it('closes on Escape', async () => {
    renderBreadcrumb(WF_CURRENT);
    await userEvent.click(await screen.findByRole('button', { name: /Reports/ }));
    await screen.findByTestId('folder-contents-browser');

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByTestId('folder-contents-browser')).toBeNull();
  });

  it('closes on outside pointerdown', async () => {
    renderBreadcrumb(WF_CURRENT);
    await userEvent.click(await screen.findByRole('button', { name: /Reports/ }));
    await screen.findByTestId('folder-contents-browser');

    fireEvent.pointerDown(document.body);
    expect(screen.queryByTestId('folder-contents-browser')).toBeNull();
  });
});
