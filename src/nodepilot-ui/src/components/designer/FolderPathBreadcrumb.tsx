import { DataBase } from '@carbon/icons-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { sharedFoldersApi } from '../../api/sharedFolders';
import type { Workflow } from '../../types/api';
import { BreadcrumbSegment, type BreadcrumbSegmentData } from './BreadcrumbSegment';

const OPEN_DELAY_MS = 250;
const CLOSE_DELAY_MS = 150;

interface Props {
  workflow: Workflow;
  currentWorkflowId: string | undefined;
  onOpenWorkflow: (w: Workflow) => void;
}

/**
 * Top-left canvas overlay showing the open workflow's folder path. Each named segment is a
 * hoverable/clickable folder; its popover lists that folder's sub-folders and workflows so
 * the user can browse and jump straight from the canvas. Display is derived from
 * `workflow.folderPath` (== the folder's `Path`); only segments whose folder is in the
 * RBAC-visible folder list are interactive. Root is a static home indicator (no popover).
 */
export function FolderPathBreadcrumb({ workflow, currentWorkflowId, onOpenWorkflow }: Readonly<Props>) {
  const { t } = useTranslation('designer');

  const { data: folders = [] } = useQuery({
    queryKey: ['shared-folders'],
    queryFn: () => sharedFoldersApi.list(),
    staleTime: 30_000,
  });
  const { data: allWorkflows = [] } = useQuery({
    queryKey: ['workflows'],
    queryFn: () => api.get<Workflow[]>('/workflows'),
    staleTime: 30_000,
  });

  // The global list is capped (recent 500 by UpdatedAt) — make sure the current workflow is
  // always present in its own folder's popover even if it falls outside that window.
  const workflows = useMemo(
    () => (allWorkflows.some((w) => w.id === workflow.id) ? allWorkflows : [...allWorkflows, workflow]),
    [allWorkflows, workflow],
  );

  const byPath = useMemo(() => new Map(folders.map((f) => [f.path, f] as const)), [folders]);

  const segments = useMemo<BreadcrumbSegmentData[]>(() => {
    const names = (workflow.folderPath ?? '').split('/').filter(Boolean);
    return names.map((name, i) => {
      const path = `/${names.slice(0, i + 1).join('/')}`;
      return { name, path, folder: byPath.get(path) ?? null };
    });
  }, [workflow.folderPath, byPath]);

  const [openPath, setOpenPath] = useState<string | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const openTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const clearTimers = () => {
    for (const ref of [openTimer, closeTimer]) {
      if (ref.current) {
        clearTimeout(ref.current);
        ref.current = null;
      }
    }
  };
  useEffect(() => () => clearTimers(), []);

  const cancelClose = () => {
    if (closeTimer.current) {
      clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
  };
  const requestOpen = (path: string) => {
    cancelClose();
    if (openPath === path) return;
    if (openTimer.current) clearTimeout(openTimer.current);
    openTimer.current = setTimeout(() => setOpenPath(path), OPEN_DELAY_MS);
  };
  const requestClose = () => {
    if (openTimer.current) {
      clearTimeout(openTimer.current);
      openTimer.current = null;
    }
    if (closeTimer.current) clearTimeout(closeTimer.current);
    closeTimer.current = setTimeout(() => setOpenPath(null), CLOSE_DELAY_MS);
  };
  const toggle = (path: string) => {
    clearTimers();
    setOpenPath((cur) => (cur === path ? null : path));
  };

  // Outside-pointerdown + Escape close — covers touch tap-away and keyboard, since the
  // hover bridge alone can't dismiss a click/keyboard-opened popover.
  useEffect(() => {
    if (!openPath) return;
    const onDown = (e: PointerEvent) => {
      const target = e.target as Node;
      if (containerRef.current?.contains(target)) return;
      if (popoverRef.current?.contains(target)) return;
      setOpenPath(null);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpenPath(null);
    };
    document.addEventListener('pointerdown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('pointerdown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [openPath]);

  if (segments.length === 0) return null;

  return (
    <div
      ref={containerRef}
      className="flex max-w-full items-center gap-1 overflow-x-auto rounded-lg border border-outline-variant/20 bg-surface-lowest/90 px-2 py-1 text-xs font-label shadow-sm backdrop-blur"
      data-testid="folder-path-breadcrumb"
    >
      <DataBase size={12} className="shrink-0 text-primary" aria-label={t('folderPath.homeLabel')} />
      {segments.map((segment) => (
        <BreadcrumbSegment
          key={segment.path}
          segment={segment}
          isOpen={openPath === segment.path}
          popoverRef={popoverRef}
          folders={folders}
          workflows={workflows}
          currentWorkflowId={currentWorkflowId}
          onOpenWorkflow={onOpenWorkflow}
          onEnter={requestOpen}
          onLeave={requestClose}
          onToggle={toggle}
          onPopoverEnter={cancelClose}
          onPopoverLeave={requestClose}
        />
      ))}
    </div>
  );
}
