import { ChevronRight } from '@carbon/icons-react';
import { useRef, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import type { SharedFolder } from '../../api/sharedFolders';
import type { Workflow } from '../../types/api';
import { AnchoredPickerPopover } from './properties/AnchoredPickerPopover';
import { FolderContentsBrowser } from './FolderContentsBrowser';

export interface BreadcrumbSegmentData {
  /** Folder name as shown in the path. */
  name: string;
  /** Cumulative folder path, e.g. "/Finance/Reports" — also the open-state key. */
  path: string;
  /** Resolved folder, or null when the ancestor is not RBAC-visible (→ non-interactive). */
  folder: SharedFolder | null;
}

interface Props {
  segment: BreadcrumbSegmentData;
  isOpen: boolean;
  /** Shared across segments — only one popover is mounted at a time. */
  popoverRef: RefObject<HTMLDivElement | null>;
  folders: SharedFolder[];
  workflows: Workflow[];
  currentWorkflowId: string | undefined;
  onOpenWorkflow: (w: Workflow) => void;
  onEnter: (path: string) => void;
  onLeave: () => void;
  onToggle: (path: string) => void;
  onPopoverEnter: () => void;
  onPopoverLeave: () => void;
}

/**
 * A single clickable/hoverable breadcrumb folder segment. Owns its own anchor ref (hooks
 * can't run in a `.map`), and renders the folder-contents popover when open. A segment whose
 * folder couldn't be resolved (RBAC-hidden ancestor) renders as plain, non-interactive text.
 */
export function BreadcrumbSegment({
  segment, isOpen, popoverRef, folders, workflows, currentWorkflowId, onOpenWorkflow,
  onEnter, onLeave, onToggle, onPopoverEnter, onPopoverLeave,
}: Readonly<Props>) {
  const { t } = useTranslation('designer');
  const anchorRef = useRef<HTMLButtonElement>(null);
  const folder = segment.folder;

  return (
    <span className="flex shrink-0 items-center gap-1">
      <ChevronRight size={11} className="shrink-0 text-on-surface-variant/50" aria-hidden />
      {folder ? (
        <>
          <button
            ref={anchorRef}
            type="button"
            aria-expanded={isOpen}
            aria-label={t('folderPath.browseFolder', { name: segment.name })}
            className={`max-w-[10rem] truncate rounded px-1.5 py-0.5 transition-colors ${
              isOpen ? 'bg-primary-fixed text-on-primary-fixed' : 'text-on-surface hover:bg-surface-high'
            }`}
            onMouseEnter={() => onEnter(segment.path)}
            onMouseLeave={onLeave}
            onClick={() => onToggle(segment.path)}
          >
            {segment.name}
          </button>
          <AnchoredPickerPopover open={isOpen} anchorRef={anchorRef} popoverRef={popoverRef}>
            <FolderContentsBrowser
              startFolderId={folder.id}
              folders={folders}
              workflows={workflows}
              currentWorkflowId={currentWorkflowId}
              onOpenWorkflow={onOpenWorkflow}
              onMouseEnter={onPopoverEnter}
              onMouseLeave={onPopoverLeave}
            />
          </AnchoredPickerPopover>
        </>
      ) : (
        <span
          className="max-w-[10rem] cursor-default truncate px-1.5 py-0.5 text-on-surface-variant/70"
          title={t('folderPath.notAccessible')}
        >
          {segment.name}
        </span>
      )}
    </span>
  );
}
