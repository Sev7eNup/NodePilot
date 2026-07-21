import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation, Trans } from 'react-i18next';
import { api, downloadFromApi } from '../api/client';
import type { Workflow, LastExecutionInfo } from '../types/api';
import { formatDate, formatDuration, formatRelative } from '../lib/format';
import { TRIGGER_BADGE_META } from '../lib/triggerBadgeMeta';
import {
  Add, Apps, CheckmarkFilled, ChevronDown, ChevronUp, CircleDash, Copy, DocumentExport,
  Download, Edit, ErrorFilled, FlashFilled, Locked, MagicWandFilled, Play, Power,
  SubtractAlt, Time, Touch_1, TrashCan, Unlocked, Upload,
} from '@carbon/icons-react';
import { useEffect, useRef, useState, useMemo } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { RunWorkflowDialog, extractManualTriggerConfig } from '../components/common/RunWorkflowDialog';
import { WorkflowGenerationDialog } from '../components/ai/WorkflowGenerationDialog';
import { useRole } from '../lib/rbac';
import { useAuthStore } from '../stores/authStore';
import { SharedFolderTree, WORKFLOW_DRAG_MIME } from '../components/workflows/SharedFolderTree';
import { SharedFolderPermissionsModal } from '../components/workflows/SharedFolderPermissionsModal';
import { ROOT_FOLDER_ID, sharedFoldersApi, type SharedFolder } from '../api/sharedFolders';
import { ResizeHandle, CornerResizeHandle } from '../components/designer/library/NodeLibrary';
import { useResizable } from '../hooks/useResizable';
import { useIsMobile } from '../hooks/useMediaQuery';
import { MobileCardList } from '../components/common/MobileCardList';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';

type ImportedWorkflowInfo = { id: string; name: string; originalName: string | null };
type ImportResponse = { created: number; workflows: ImportedWorkflowInfo[]; errors: string[] };

type ScorchImportedWorkflowInfo = {
  id: string; name: string; originalName: string | null;
  activityCount: number; heuristicCount: number; fallbackCount: number;
};
type ScorchImportedVariableInfo = {
  name: string; originalName: string | null;
  createdNow: boolean; skipped: boolean; skipReason: string | null;
};
type ScorchImportResponse = {
  created: number;
  workflows: ScorchImportedWorkflowInfo[];
  variables: ScorchImportedVariableInfo[];
  warnings: string[];
  errors: string[];
};

function buildTriggerMeta(t: (k: string) => string): Record<string, { label: string; icon: typeof Time; className: string }> {
  return {
    ...TRIGGER_BADGE_META,
    manualTrigger: { label: t('triggers:badges.manual'), icon: Touch_1, className: 'bg-surface-container text-on-surface' },
  };
}


function LastRunCell({ info }: Readonly<{ info: LastExecutionInfo | null | undefined }>) {
  const { t } = useTranslation(['workflows', 'executions']);
  if (!info) return <span className="text-xs text-outline">{t('workflows:lastRunNever')}</span>;
  const status = info.status;
  const map: Record<string, { icon: typeof CheckmarkFilled; className: string; key: string }> = {
    Succeeded: { icon: CheckmarkFilled,  className: 'text-green-600',  key: 'succeeded' },
    Failed:    { icon: ErrorFilled,       className: 'text-red-600',    key: 'failed' },
    Running:   { icon: CircleDash,       className: 'text-blue-600 animate-spin', key: 'running' },
    Pending:   { icon: CircleDash,  className: 'text-outline',              key: 'pending' },
    Cancelled: { icon: SubtractAlt,   className: 'text-on-surface-variant',   key: 'cancelled' },
    Skipped:   { icon: SubtractAlt,   className: 'text-outline',              key: 'skipped' },
  };
  const meta = map[status] ?? { icon: CircleDash, className: 'text-outline', key: '' };
  const Icon = meta.icon;
  return (
    <div className="flex items-center gap-1.5" title={formatDate(info.startedAt)}>
      <Icon size={14} className={meta.className} />
      <span className="text-xs text-on-surface-variant">{formatRelative(info.startedAt)}</span>
    </div>
  );
}

function SuccessRateCell({ success, total }: Readonly<{ success: number; total: number }>) {
  const { t } = useTranslation(['workflows', 'common']);
  if (total === 0) return <span className="text-xs text-outline">{t('common:dash')}</span>;
  const pct = Math.round((success / total) * 100);
  const barClass = pct >= 95 ? 'np-bar-ok' : pct >= 80 ? 'np-bar-warn' : 'np-bar-bad';
  return (
    <div className="flex items-center gap-2" title={t('workflows:successRateTooltip', { success, total })}>
      <div className="w-16 h-1.5 bg-surface-container rounded-full overflow-hidden">
        <div className={`np-bar h-full ${barClass}`} style={{ width: `${pct}%` }} />
      </div>
      <span className="text-xs text-on-surface-variant tabular-nums">{success}/{total}</span>
    </div>
  );
}

export function WorkflowsPage() {
  const { t } = useTranslation(['workflows', 'common']);
  const TRIGGER_META = buildTriggerMeta(t);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { canWrite, canDelete, isAdmin } = useRole();
  const currentUserId = useAuthStore((s) => s.userId);
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState('');
  const [showAiGenerate, setShowAiGenerate] = useState(false);
  // Left-sidebar shared-folder filter (part of the folder-permissions/RBAC feature).
  // null = "All folders" (the legacy unfiltered view); a folder id pins the list to
  // that folder + descendants.
  const [selectedFolderId, setSelectedFolderId] = useState<string | null>(null);
  // Admin's "manage permissions on this folder" modal — only visible to a user with
  // FolderAdmin (capabilities.canAdmin) on the selected folder.
  const [permissionsModalFolderId, setPermissionsModalFolderId] = useState<string | null>(null);
  // Drag-resizable Shared-Folders sidebar (deep nesting + long folder names need wider view).
  // Width is always pixel-sized. Height defaults to AUTO (fits the folder list so you see
  // everything at once) and only becomes pixel-sized once the user drags the corner grip —
  // tracked by `folderHeightDirty`. The drag seeds from the measured current height
  // (`folderBoxRef`) so the first pull continues smoothly instead of jumping.
  const folderPanel = useResizable({ initialSize: 256, minSize: 180, maxSize: 600, direction: 'horizontal' });
  const folderPanelHeight = useResizable({ initialSize: 360, minSize: 160, maxSize: 800, direction: 'vertical' });
  const [folderHeightDirty, setFolderHeightDirty] = useState(false);
  const folderBoxRef = useRef<HTMLDivElement>(null);
  const isMobile = useIsMobile();
  const tbodyRef = useRef<HTMLTableSectionElement>(null);
  const [scrollMargin, setScrollMargin] = useState(0);

  // Shared-folder tree as a separate query so we can read capabilities for the selected
  // folder directly (without inferring from workflows). This way an empty folder still
  // surfaces the permissions button when the caller is FolderAdmin on it.
  const { data: sharedFolders } = useQuery({
    queryKey: ['shared-folders'],
    queryFn: () => sharedFoldersApi.list(),
    staleTime: 30_000,
  });

  // User picker for the permissions modal — pulled lazily so the page doesn't pay the
  // /api/users round-trip on every load (only callers with FolderAdmin somewhere ever
  // open the modal). Trigger as soon as any folder we know about has canAdmin — that's
  // the broadest condition under which the modal could be opened.
  const anyFolderWithAdmin = useMemo(
    () => (sharedFolders ?? []).some((f: SharedFolder) => f.capabilities.canAdmin),
    [sharedFolders],
  );
  const { data: usersForPicker } = useQuery({
    queryKey: ['users-for-permissions-picker'],
    queryFn: () => api.get<{ id: string; username: string }[]>('/users'),
    enabled: permissionsModalFolderId !== null && anyFolderWithAdmin,
    staleTime: 60_000,
  });

  const { data: workflows, isLoading } = useQuery({
    queryKey: ['workflows'],
    queryFn: () => api.get<Workflow[]>('/workflows'),
  });

  const createMutation = useMutation({
    mutationFn: (name: string) =>
      // Target the folder the user is currently viewing. Without this, a user with
      // FolderEditor on /Finance could click "New" inside /Finance and hit a 403/404
      // because the server defaults to Root (where they have no edit rights).
      api.post<Workflow>('/workflows', {
        name,
        description: '',
        definitionJson: JSON.stringify({ nodes: [], edges: [] }),
        folderId: selectedFolderId ?? undefined,
      }),
    onSuccess: (workflow) => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      setShowCreate(false);
      setNewName('');
      navigate(`/workflows/${workflow.id}`);
    },
  });

  // AI-generated workflow: saves the same way as createMutation, but with the
  // name/description/definitionJson coming from the LLM instead of the user's typed
  // name. Kept as its own mutation because the request body shape differs and the
  // dialog has its own error-state UI.
  const aiCreateMutation = useMutation({
    mutationFn: (req: { name: string; description: string; definitionJson: string }) =>
      api.post<Workflow>('/workflows', { ...req, folderId: selectedFolderId ?? undefined }),
    onSuccess: (workflow) => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      setShowAiGenerate(false);
      navigate(`/workflows/${workflow.id}`);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/workflows/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workflows'] }),
  });

  const duplicateMutation = useMutation({
    mutationFn: (id: string) => api.post<Workflow>(`/workflows/${id}/duplicate`, {}),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workflows'] }),
  });

  const enableMutation = useMutation({
    mutationFn: (id: string) => api.post(`/workflows/${id}/enable`, {}),
    onSuccess: (_, id) =>
      queryClient.setQueryData<Workflow[]>(['workflows'], old =>
        old?.map(w => w.id === id ? { ...w, isEnabled: true } : w) ?? []),
  });

  const disableMutation = useMutation({
    mutationFn: (id: string) => api.post(`/workflows/${id}/disable`, {}),
    onSuccess: (_, id) =>
      queryClient.setQueryData<Workflow[]>(['workflows'], old =>
        old?.map(w => w.id === id ? { ...w, isEnabled: false } : w) ?? []),
  });

  const forceUnlockMutation = useMutation({
    mutationFn: (id: string) => api.post(`/workflows/${id}/force-unlock`, {}),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['workflows'] }),
  });

  // Drag-and-drop: workflow row → folder tree node. The backend (POST
  // /api/workflows/{id}/move-folder) checks Edit on both source and destination, so
  // permission inheritance is automatic — once the workflow lives in the new folder,
  // the folder's grants govern who can read/run/edit it.
  const moveWorkflowMutation = useMutation({
    mutationFn: ({ workflowId, targetFolderId }: { workflowId: string; targetFolderId: string }) =>
      sharedFoldersApi.moveWorkflowToFolder(workflowId, targetFolderId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
    },
    onError: (e: Error) => {
      toast.error(t('workflows:moveFailed', { msg: e.message }));
    },
  });

  const importInputRef = useRef<HTMLInputElement>(null);
  // Multi-file import: posts each selected file as its own envelope (the server's
  // /workflows/import accepts one envelope per call) and aggregates the per-file
  // results into one summary. A single file with a JSON-parse error or HTTP failure
  // does NOT abort the batch — operators selecting 20 exports don't want one bad
  // file to nuke the run, they want a final report telling them which ones to retry.
  type PerFileResult =
    | { kind: 'ok'; file: string; resp: ImportResponse }
    | { kind: 'fail'; file: string; message: string };
  // Mirror the backend MaxRequestBodyBytes guard (6 MiB on /api/workflows/import).
  // Without an early frontend check the Browser reads gigabyte-files into a string before
  // we even start the upload — that pegs Tab-Memory and gives the user a confusing OOM
  // tab-crash instead of a clean "file too large" message.
  const MAX_IMPORT_BYTES = 6 * 1024 * 1024;
  const importMutation = useMutation({
    mutationFn: async (files: File[]): Promise<PerFileResult[]> => {
      const results: PerFileResult[] = [];
      for (const file of files) {
        try {
          if (file.size > MAX_IMPORT_BYTES) {
            throw new Error(t('workflows:importFileTooLarge', {
              file: file.name,
              maxMb: Math.floor(MAX_IMPORT_BYTES / (1024 * 1024)),
            }));
          }
          const text = await file.text();
          let envelope: unknown;
          try {
            envelope = JSON.parse(text);
          } catch {
            throw new Error(t('workflows:importInvalidJson', { file: file.name }));
          }
          // Import lands in the currently selected folder (Root when "all" is selected) —
          // same targeting as Create; the server enforces Edit on that folder.
          const importUrl = selectedFolderId
            ? `/workflows/import?folderId=${selectedFolderId}`
            : '/workflows/import';
          const resp = await api.post<ImportResponse>(importUrl, envelope);
          results.push({ kind: 'ok', file: file.name, resp });
        } catch (err) {
          results.push({ kind: 'fail', file: file.name, message: (err as Error).message });
        }
      }
      return results;
    },
    onSuccess: (results) => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });

      const lines: string[] = [];
      const okResults = results.filter((r): r is Extract<PerFileResult, { kind: 'ok' }> => r.kind === 'ok');
      const failResults = results.filter((r): r is Extract<PerFileResult, { kind: 'fail' }> => r.kind === 'fail');

      if (results.length === 1 && okResults.length === 1) {
        // Single-file path keeps the original phrasing so existing translations stay accurate.
        const only = okResults[0];
        const renamed = only.resp.workflows.filter(w => w.originalName).map(w => `"${w.originalName}" → "${w.name}"`);
        lines.push(t('workflows:importedSummary', { count: only.resp.created, file: only.file }));
        if (renamed.length > 0) lines.push('', t('workflows:renamedHeader'), ...renamed);
        if (only.resp.errors.length > 0) lines.push('', t('workflows:errorsHeader'), ...only.resp.errors);
      } else {
        // Multi-file: one top-line tally + per-file detail block.
        const totalCreated = okResults.reduce((sum, r) => sum + r.resp.created, 0);
        lines.push(t('workflows:importedSummaryBatch', {
          count: totalCreated,
          fileCount: results.length,
          okCount: okResults.length,
          failCount: failResults.length,
        }));
        for (const r of okResults) {
          lines.push('', `• ${r.file}: ${t('workflows:importedSummary', { count: r.resp.created, file: r.file })}`);
          const renamed = r.resp.workflows.filter(w => w.originalName).map(w => `   "${w.originalName}" → "${w.name}"`);
          if (renamed.length > 0) lines.push(t('workflows:renamedHeader'), ...renamed);
          if (r.resp.errors.length > 0) lines.push(t('workflows:errorsHeader'), ...r.resp.errors.map(e => `   ${e}`));
        }
        for (const r of failResults) {
          lines.push('', `✗ ${r.file}: ${r.message}`);
        }
      }
      const hasFailures = failResults.length > 0 || okResults.some((r) => r.resp.errors.length > 0);
      if (hasFailures) {
        // Failure reports must not vanish after the default 4s — keep them up long enough to read.
        toast.error(lines.join('\n'), 30_000);
      } else {
        toast.success(lines.join('\n'));
      }
    },
    onError: (err: Error) => toast.error(t('common:importFailed', { message: err.message })),
  });

  // Importing from System Center Orchestrator (SCOrch) produces much more detail
  // than a normal import (per-runbook warnings, counts of heuristic guesses) — a
  // modal surfaces all of that instead of hiding it in a plain alert().
  const scorchInputRef = useRef<HTMLInputElement>(null);
  const [scorchResult, setScorchResult] = useState<{ resp: ScorchImportResponse; filename: string } | null>(null);
  // SCOrch imports allow a more generous 50 MiB (see ScorchImporter.cs on the backend) —
  // mirrored here so the browser tab doesn't crash with an out-of-memory error if
  // someone accidentally uploads a multi-gigabyte backup file instead.
  const MAX_SCORCH_BYTES = 50 * 1024 * 1024;
  const scorchMutation = useMutation({
    mutationFn: async (file: File): Promise<ScorchImportResponse> => {
      if (file.size > MAX_SCORCH_BYTES) {
        throw new Error(t('workflows:importFileTooLarge', {
          file: file.name,
          maxMb: Math.floor(MAX_SCORCH_BYTES / (1024 * 1024)),
        }));
      }
      const text = await file.text();
      const scorchUrl = selectedFolderId
        ? `/workflows/import-scorch?folderId=${selectedFolderId}`
        : '/workflows/import-scorch';
      return api.postRaw<ScorchImportResponse>(scorchUrl, text, 'application/xml');
    },
    onSuccess: (resp, file) => {
      queryClient.invalidateQueries({ queryKey: ['workflows'] });
      setScorchResult({ resp, filename: file.name });
    },
    onError: (err: Error) => toast.error(t('common:importFailed', { message: err.message })),
  });
  const handleScorchClick = () => scorchInputRef.current?.click();
  const handleScorchFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) scorchMutation.mutate(file);
    e.target.value = '';
  };

  const handleExportOne = async (w: Workflow) => {
    try {
      await downloadFromApi(`/workflows/${w.id}/export`, `${w.name}.workflow.json`);
    } catch (err) {
      toast.error(t('common:exportFailed', { message: (err as Error).message }));
    }
  };
  const handleExportAll = async () => {
    try {
      await downloadFromApi('/workflows/export', 'nodepilot-workflows.json');
    } catch (err) {
      toast.error(t('common:exportFailed', { message: (err as Error).message }));
    }
  };
  const handleImportClick = () => importInputRef.current?.click();
  const handleImportFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files ? Array.from(e.target.files) : [];
    if (files.length > 0) importMutation.mutate(files);
    // Always reset so re-selecting the same file(s) fires onChange again.
    e.target.value = '';
  };

  const [runDialogWorkflow, setRunDialogWorkflow] = useState<Workflow | null>(null);

  // --- Column resizing ---
  // ColKey covers all columns (sort uses it). ResizableColKey excludes Name —
  // Name auto-flexes to fill remaining horizontal space under table-layout:fixed,
  // so it has no inline width and no drag-handle.
  type ColKey = 'name' | 'activities' | 'triggers' | 'status' | 'lastRun' | 'successRate' | 'runtime' | 'created' | 'updated';
  type ResizableColKey = Exclude<ColKey, 'name'>;
  const NAME_MIN_WIDTH = 280;
  const ACTIONS_WIDTH = 220; // ≤7 buttons × ~28px + gap-1 + px-4 cell padding
  const DEFAULT_WIDTHS: Record<ResizableColKey, number> = {
    activities: 90, triggers: 195, status: 160,
    lastRun: 110, successRate: 130, runtime: 90, created: 170, updated: 170,
  };
  // Per-column minimum resize width. Status needs enough room for the longest German
  // status badge text ("Fehlgeschlagen" = "Failed", or a locked-workflow badge) —
  // otherwise the non-wrapping pill gets clipped when the column is dragged narrow.
  const MIN_WIDTHS: Partial<Record<ResizableColKey, number>> = { status: 160 };
  const [colWidths, setColWidths] = useState(DEFAULT_WIDTHS);
  const resizeRef = useRef<{ col: ResizableColKey; startX: number; startWidth: number } | null>(null);

  const startResize = (col: ResizableColKey, e: React.MouseEvent) => {
    e.preventDefault();
    resizeRef.current = { col, startX: e.clientX, startWidth: colWidths[col] };
    const onMove = (ev: MouseEvent) => {
      if (!resizeRef.current) return;
      const { col, startWidth, startX } = resizeRef.current;
      const w = Math.max(MIN_WIDTHS[col] ?? 50, startWidth + ev.clientX - startX);
      setColWidths(prev => ({ ...prev, [col]: w }));
    };
    const onUp = () => {
      resizeRef.current = null;
      globalThis.removeEventListener('mousemove', onMove);
      globalThis.removeEventListener('mouseup', onUp);
    };
    globalThis.addEventListener('mousemove', onMove);
    globalThis.addEventListener('mouseup', onUp);
  };

  // --- Column sorting ---
  const [sortBy, setSortBy] = useState<ColKey | null>(null);
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');

  const handleSort = (col: ColKey) => {
    if (sortBy === col) setSortDir(d => d === 'asc' ? 'desc' : 'asc');
    else { setSortBy(col); setSortDir('asc'); }
  };

  // Folder filter: null = no filter (all visible workflows). A selected folder id
  // shows only workflows directly in that folder (exact folderId match) — subfolders
  // are not included. Consistent with WorkflowBrowser in the designer.
  const filteredWorkflows = useMemo(() => {
    if (!workflows) return [];
    if (!selectedFolderId) return workflows;
    return workflows.filter(w => w.folderId === selectedFolderId);
  }, [workflows, selectedFolderId]);

  const sortedWorkflows = useMemo(() => {
    if (!filteredWorkflows.length || !sortBy) return filteredWorkflows;
    return [...filteredWorkflows].sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'name':        cmp = a.name.localeCompare(b.name); break;
        case 'activities':  cmp = (a.activityCount ?? 0) - (b.activityCount ?? 0); break;
        case 'status':      cmp = Number(b.isEnabled) - Number(a.isEnabled); break;
        case 'lastRun':     cmp = (a.lastExecution?.startedAt ?? '').localeCompare(b.lastExecution?.startedAt ?? ''); break;
        case 'successRate': {
          const ra = (a.totalCount ?? 0) > 0 ? (a.successCount ?? 0) / a.totalCount! : -1;
          const rb = (b.totalCount ?? 0) > 0 ? (b.successCount ?? 0) / b.totalCount! : -1;
          cmp = ra - rb; break;
        }
        case 'runtime':  cmp = (a.avgDurationMs ?? -1) - (b.avgDurationMs ?? -1); break;
        case 'created':  cmp = a.createdAt.localeCompare(b.createdAt); break;
        case 'updated':  cmp = a.updatedAt.localeCompare(b.updatedAt); break;
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
  }, [filteredWorkflows, sortBy, sortDir]);

  // Measure the scroll offset between #np-main-scroll and the tbody after the table
  // renders. Re-runs when loading completes (table appears) or mobile/desktop switches.
  useEffect(() => {
    if (isMobile) return;
    const scroll = document.getElementById('np-main-scroll');
    const tbody = tbodyRef.current;
    if (!scroll || !tbody) return;
    setScrollMargin(
      Math.round(tbody.getBoundingClientRect().top - scroll.getBoundingClientRect().top + scroll.scrollTop)
    );
   
  }, [isLoading, isMobile]);

  const rowVirtualizer = useVirtualizer({
    count: isMobile ? 0 : sortedWorkflows.length,
    getScrollElement: () => document.getElementById('np-main-scroll'),
    estimateSize: () => 52,
    overscan: 8,
    getItemKey: (index) => sortedWorkflows[index].id,
    measureElement: (el) => el?.getBoundingClientRect().height ?? 52,
    scrollMargin,
    // jsdom has no layout (height = 0) → virtualizer renders 0 items → tests fail.
    // A generous initialRect makes all test-sized datasets (≤ ~30 items) fully visible
    // until the real scroll element provides its actual rect.
    initialRect: { width: 1200, height: 900 },
  });

  const executeMutation = useMutation({
    mutationFn: ({ id, params }: { id: string; params?: Record<string, string> }) =>
      api.post(`/workflows/${id}/execute`, params ? { parameters: params } : {}),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['executions'] }),
  });

  const handleRunWorkflow = (w: Workflow) => {
    const triggerConfig = extractManualTriggerConfig(w.definitionJson);
    if (triggerConfig && triggerConfig.parameters.length > 0) {
      setRunDialogWorkflow(w);
    } else {
      executeMutation.mutate({ id: w.id });
    }
  };

  // Per-row RBAC: prefer the server-supplied capabilities, fall back to the global
  // role flags for legacy responses that didn't surface them. The server already ANDs
  // capabilities with the global UserRole cap, so a global Viewer with FolderEditor on
  // /Finance ends up with canRun=false/canEdit=false here too — the buttons stay
  // hidden, matching what the API would 403 anyway. Where capabilities widen access
  // is the opposite case: a global Operator who has FolderEditor on /Finance only
  // sees edit buttons exclusively on /Finance rows, not on /Sales.
  const rowCanRun    = (w: Workflow) => w.capabilities ? w.capabilities.canRun  : canWrite;
  const rowCanEdit   = (w: Workflow) => w.capabilities ? w.capabilities.canEdit : canWrite;
  // canDelete is its own capability from the server (the controller enforces Admin-only).
  // A frontend heuristic based on canEdit alone was deliberately not used, because
  // Operators with Folder-Editor rights also have canEdit=true but must not see Delete.
  const rowCanDelete = (w: Workflow) => w.capabilities ? w.capabilities.canDelete : canDelete;
  const rowCanForceUnlock = (w: Workflow) => w.capabilities ? w.capabilities.canAdmin : isAdmin;

  // Import/SCOrch-Import target the currently selected folder (Root when "all" is
  // selected) — like Create. Gate the buttons on the caller's effective Edit on THAT
  // folder, not on global canWrite — otherwise a scoped Operator without the grant
  // clicks Import and hits a 403. Capabilities come from the shared-folders list;
  // fall back to canWrite while the list is loading so the button doesn't flicker.
  const canEditImportTarget = useMemo(() => {
    const targetId = selectedFolderId ?? ROOT_FOLDER_ID;
    const target = sharedFolders?.find((f: SharedFolder) => f.id === targetId);
    return target ? target.capabilities.canEdit : canWrite;
  }, [sharedFolders, canWrite, selectedFolderId]);

  return (
    <div>
      <div className="flex items-center justify-end mb-6">
        <div className="flex flex-wrap items-center justify-end gap-2">
          <input
            ref={importInputRef}
            type="file"
            accept="application/json,.json"
            multiple
            className="hidden"
            onChange={handleImportFile}
          />
          {canEditImportTarget && (
            <button
              onClick={handleImportClick}
              disabled={importMutation.isPending}
              className="flex items-center gap-2 px-3 py-2 bg-surface-lowest border border-outline-variant text-on-surface rounded-md hover:bg-surface-low transition-colors text-sm disabled:opacity-50"
              title={t('workflows:importTitle')}
            >
              <Upload size={16} /> <span className="hidden sm:inline">{importMutation.isPending ? t('common:importing') : t('common:import')}</span>
            </button>
          )}
          <input
            ref={scorchInputRef}
            type="file"
            accept=".ois_export,.ore,application/xml,text/xml,.xml"
            className="hidden"
            onChange={handleScorchFile}
          />
          {canEditImportTarget && (
            <button
              onClick={handleScorchClick}
              disabled={scorchMutation.isPending}
              className="flex items-center gap-2 px-3 py-2 bg-surface-lowest border border-outline-variant text-on-surface rounded-md hover:bg-surface-low transition-colors text-sm disabled:opacity-50"
              title={t('workflows:importScorchTitle')}
            >
              <DocumentExport size={16} /> <span className="hidden sm:inline">{scorchMutation.isPending ? t('common:importing') : t('workflows:importScorch')}</span>
            </button>
          )}
          <button
            onClick={handleExportAll}
            disabled={!workflows || workflows.length === 0}
            className="flex items-center gap-2 px-3 py-2 bg-surface-lowest border border-outline-variant text-on-surface rounded-md hover:bg-surface-low transition-colors text-sm disabled:opacity-50"
            title={t('workflows:exportAllTitle')}
          >
            <Download size={16} /> <span className="hidden sm:inline">{t('common:exportAll')}</span>
          </button>
          {canWrite && (
            <button
              onClick={() => setShowAiGenerate(true)}
              className="flex items-center gap-2 px-3 py-2 bg-violet-600 text-white rounded-md hover:bg-violet-700 transition-colors text-sm"
              title={t('workflows:newAiWorkflowTitle')}
            >
              <MagicWandFilled size={16} /> <span className="hidden sm:inline">{t('workflows:newAiWorkflow')}</span>
            </button>
          )}
          {canWrite && (
            <button
              onClick={() => setShowCreate(true)}
              className="flex items-center gap-2 px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors text-sm"
            >
              <Add size={16} /> <span className="hidden sm:inline">{t('workflows:newWorkflow')}</span>
            </button>
          )}
        </div>
      </div>

      {showCreate && (
        <div className="np-card p-4 mb-4 flex gap-3 np-fade-up">
          <input
            type="text"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            placeholder={t('workflows:workflowName')}
            className="flex-1 px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            autoFocus
            onKeyDown={(e) => e.key === 'Enter' && newName && createMutation.mutate(newName)}
          />
          <button
            onClick={() => newName && createMutation.mutate(newName)}
            className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm"
          >
            {t('common:create')}
          </button>
          <button
            onClick={() => { setShowCreate(false); setNewName(''); }}
            className="px-4 py-2 bg-surface-container text-on-surface rounded-md hover:bg-surface-high text-sm"
          >
            {t('common:cancel')}
          </button>
        </div>
      )}

      <div className="flex flex-col lg:flex-row">
        {/* Org-level shared-folder tree. Desktop: a resizable left rail. Mobile:
            a collapsible disclosure stacked above the list so it doesn't steal the
            whole first screen. Selecting a folder scopes the list to it + descendants;
            the tree is permission-filtered server-side. */}
        {(() => {
          const folderTree = (
            <>
              <SharedFolderTree
                selectedFolderId={selectedFolderId}
                onFolderSelected={setSelectedFolderId}
                onTreeMutated={() => {
                  queryClient.invalidateQueries({ queryKey: ['workflows'] });
                  queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
                }}
                onWorkflowDropped={(workflowId, targetFolderId) => {
                  // Skip a no-op move when the workflow is already in the target folder —
                  // saves a round-trip and keeps the audit log clean.
                  const wf = workflows?.find((w) => w.id === workflowId);
                  if (wf && (wf.folderId ?? ROOT_FOLDER_ID) === targetFolderId) return;
                  moveWorkflowMutation.mutate({ workflowId, targetFolderId });
                }}
              />
              {selectedFolderId && (() => {
                // Show "manage permissions" only when the caller has Admin on the selected
                // folder. Read capabilities directly from the folder list — an earlier version
                // inferred them from workflows in the folder, which broke for empty folders
                // even when the caller actually had FolderAdmin.
                const folder = sharedFolders?.find((f: SharedFolder) => f.id === selectedFolderId);
                const canAdmin = folder?.capabilities.canAdmin ?? isAdmin;
                if (!canAdmin) return null;
                return (
                  <button
                    onClick={() => setPermissionsModalFolderId(selectedFolderId)}
                    className="mt-3 w-full px-3 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors text-sm"
                  >
                    {t('workflows:managePermissions')}
                  </button>
                );
              })()}
            </>
          );
          if (isMobile) {
            return (
              <details className="np-card p-3 mb-3">
                <summary className="cursor-pointer select-none text-sm font-medium text-on-surface-variant">
                  {t('workflows:foldersHeading')}
                </summary>
                <div className="mt-2">{folderTree}</div>
              </details>
            );
          }
          return (
            <>
              <div
                ref={folderBoxRef}
                className="relative shrink-0 sticky top-0 self-start"
                style={{
                  width: folderPanel.size,
                  // Auto until dragged → box is exactly as tall as the folder list.
                  height: folderHeightDirty ? folderPanelHeight.size : undefined,
                  maxHeight: 'calc(100vh - 3rem)',
                }}
              >
                <aside className="np-card np-folder-card p-3 h-full w-full overflow-auto">
                  {folderTree}
                </aside>
                <CornerResizeHandle
                  title={t('workflows:folder.resizeHandleTitle')}
                  onMouseDown={(e) => {
                    // Seed the vertical drag from the box's current (auto) height so it doesn't
                    // jump to initialSize on the first pull; from here on height is pixel-driven.
                    const startH = folderBoxRef.current?.getBoundingClientRect().height;
                    folderPanel.handleProps.onMouseDown(e);
                    folderPanelHeight.handleProps.onMouseDown(e, startH);
                    setFolderHeightDirty(true);
                    document.body.style.cursor = 'nwse-resize';
                  }}
                  onDoubleClick={() => {
                    folderPanel.handleProps.onDoubleClick();
                    setFolderHeightDirty(false); // back to auto/content height
                  }}
                />
              </div>
              <ResizeHandle direction="horizontal" {...folderPanel.handleProps} />
            </>
          );
        })()}

      <div className="flex-1 min-w-0 lg:ml-3">
      {isLoading ? (
        <p className="text-outline">{t('common:loadingDots')}</p>
      ) : sortedWorkflows.length === 0 ? (
        <div className="np-card p-8 text-center text-outline np-fade-up">
          {selectedFolderId
            ? t('workflows:noWorkflowsInFolder')
            : t('workflows:noWorkflows')}
        </div>
      ) : isMobile ? (
        <MobileCardList
          items={sortedWorkflows}
          getKey={(w) => w.id}
          onRowClick={(w) => navigate(`/workflows/${w.id}`)}
          renderTitle={(w) => (
            <div className="min-w-0">
              <div className="flex items-center gap-2 min-w-0">
                <span className="text-sm font-semibold text-on-surface truncate">{w.name}</span>
                <span className="text-[10px] font-medium text-outline bg-surface-container rounded px-1.5 py-0.5 shrink-0">v{w.version}</span>
              </div>
              {w.description && <p className="text-xs text-outline mt-0.5 truncate" title={w.description}>{w.description}</p>}
            </div>
          )}
          renderFields={(w) => {
            const lockedByMe = !!w.checkedOutByUserId && w.checkedOutByUserId === currentUserId;
            const lockedByOther = !!w.checkedOutByUserId && !lockedByMe;
            const statusBadge = lockedByMe ? (
              <span className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium bg-blue-500/15 text-blue-600 dark:text-blue-400" title={t('workflows:statusEditingTitle')}>
                <Edit size={11} /> {t('workflows:statusEditing')}
              </span>
            ) : lockedByOther ? (
              <span className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium bg-amber-500/15 text-amber-600 dark:text-amber-400"
                    title={t('workflows:statusLockedByTitle', { user: w.checkedOutByUserName ?? t('common:unknown'), time: w.checkedOutAt ? formatDate(w.checkedOutAt) : t('common:unknown') })}>
                <Locked size={11} className="shrink-0" /> <span className="max-w-[110px] truncate">{w.checkedOutByUserName ?? t('workflows:statusLocked')}</span>
              </span>
            ) : (
              <span className={`inline-flex shrink-0 items-center whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium ${w.isEnabled ? 'bg-green-500/15 text-green-600 dark:text-green-400' : 'bg-surface-container text-on-surface-variant'}`}>
                {w.isEnabled ? t('workflows:statusProductive') : t('workflows:statusDisabled')}
              </span>
            );
            return [
              { label: t('workflows:tableHeaders.status'), value: statusBadge },
              {
                label: t('workflows:tableHeaders.activities'),
                value: <span className="inline-flex items-center gap-1.5 text-sm text-on-surface-variant"><Apps size={14} className="text-outline" /><span className="tabular-nums">{w.activityCount ?? 0}</span></span>,
              },
              {
                label: t('workflows:tableHeaders.triggers'),
                value: w.triggerTypes && w.triggerTypes.length > 0 ? (
                  <div className="flex flex-wrap gap-1">
                    {w.triggerTypes.map((tt) => {
                      const meta = TRIGGER_META[tt] ?? { label: tt, icon: FlashFilled, className: 'bg-surface-container text-on-surface' };
                      const Icon = meta.icon;
                      return (
                        <span key={tt} className={`inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[11px] font-medium ${meta.className}`} title={tt}>
                          <Icon size={11} /> {meta.label}
                        </span>
                      );
                    })}
                  </div>
                ) : <span className="text-xs text-outline">–</span>,
              },
              { label: t('workflows:tableHeaders.lastRun'), value: <LastRunCell info={w.lastExecution} /> },
              { label: t('workflows:tableHeaders.successRate'), value: <SuccessRateCell success={w.successCount ?? 0} total={w.totalCount ?? 0} /> },
              { label: t('workflows:tableHeaders.runtime'), value: <span className="text-sm text-on-surface-variant tabular-nums">{formatDuration(w.avgDurationMs)}</span> },
              {
                label: t('workflows:tableHeaders.created'),
                value: <span className="text-xs text-outline">{formatDate(w.createdAt)}{w.createdBy ? ` · ${t('common:by')} ${w.createdBy}` : ''}</span>,
              },
              {
                label: t('workflows:tableHeaders.updated'),
                value: <span className="text-xs text-outline">{formatDate(w.updatedAt)}{w.updatedBy ? ` · ${t('common:by')} ${w.updatedBy}` : ''}</span>,
              },
            ];
          }}
          renderActions={(w) => {
            const lockedByMe = !!w.checkedOutByUserId && w.checkedOutByUserId === currentUserId;
            const lockedByOther = !!w.checkedOutByUserId && !lockedByMe;
            return (
              <>
                {rowCanRun(w) && w.isEnabled && !w.checkedOutByUserId && (
                  <button onClick={() => handleRunWorkflow(w)} className="p-2 text-green-600 hover:bg-green-500/15 rounded-lg" title={t('workflows:actionRun')}>
                    <Play size={16} />
                  </button>
                )}
                {rowCanEdit(w) && !lockedByOther && (
                  <button
                    onClick={() => w.isEnabled ? disableMutation.mutate(w.id) : enableMutation.mutate(w.id)}
                    disabled={enableMutation.isPending || disableMutation.isPending || lockedByMe}
                    title={lockedByMe ? t('workflows:actionEnableLockedHint') : (w.isEnabled ? t('workflows:actionDisable') : t('workflows:actionEnable'))}
                    className={`p-2 rounded-lg transition-colors disabled:opacity-40 ${w.isEnabled ? 'text-green-500 hover:text-red-500 hover:bg-red-500/15' : 'text-gray-400 hover:text-green-500 hover:bg-green-500/15'}`}
                  >
                    <Power size={15} />
                  </button>
                )}
                {rowCanForceUnlock(w) && lockedByOther && (
                  <button
                    onClick={async () => { if (await confirmDialog(t('workflows:forceUnlockConfirm', { name: w.name, user: w.checkedOutByUserName ?? t('common:unknown') }))) forceUnlockMutation.mutate(w.id); }}
                    disabled={forceUnlockMutation.isPending}
                    className="p-2 text-amber-600 hover:bg-amber-500/15 rounded-lg disabled:opacity-40"
                    title={t('workflows:actionForceUnlock')}
                  >
                    <Unlocked size={15} />
                  </button>
                )}
                <button onClick={() => navigate(`/workflows/${w.id}`)} className="p-2 text-blue-600 hover:bg-blue-50 rounded-lg" title={rowCanEdit(w) ? t('workflows:actionEdit') : t('workflows:actionView')}>
                  <Edit size={16} />
                </button>
                {rowCanEdit(w) && (
                  <button onClick={() => duplicateMutation.mutate(w.id)} className="p-2 text-indigo-600 hover:bg-indigo-500/15 rounded-lg" title={t('workflows:actionDuplicate')}>
                    <Copy size={16} />
                  </button>
                )}
                <button onClick={() => handleExportOne(w)} className="p-2 text-on-surface-variant hover:bg-surface-container rounded-lg" title={t('workflows:actionExport')}>
                  <Download size={16} />
                </button>
                {rowCanDelete(w) && (
                  <button onClick={async () => { if (await confirmDialog({ message: t('workflows:deleteConfirm'), danger: true })) deleteMutation.mutate(w.id); }} className="p-2 text-red-600 hover:bg-red-500/15 rounded-lg" title={t('workflows:actionDelete')}>
                    <TrashCan size={16} />
                  </button>
                )}
              </>
            );
          }}
        />
      ) : (
        <div className="np-card overflow-hidden np-fade-up">
          <div className="overflow-x-auto">
          <table
            style={{
              tableLayout: 'fixed',
              width: '100%',
              minWidth:
                Object.values(colWidths).reduce((a, b) => a + b, 0)
                + ACTIONS_WIDTH
                + NAME_MIN_WIDTH,
            }}
          >
            <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
              <tr>
                <th style={{ minWidth: NAME_MIN_WIDTH }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                  <button
                    onClick={() => handleSort('name')}
                    className="flex items-center gap-1 hover:text-on-surface transition-colors"
                  >
                    {t('workflows:tableHeaders.name')}
                    {sortBy === 'name'
                      ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                      : <span className="w-3" />}
                  </button>
                </th>
                {([
                  ['activities', t('workflows:tableHeaders.activities')], ['triggers', t('workflows:tableHeaders.triggers')],
                  ['status', t('workflows:tableHeaders.status')], ['lastRun', t('workflows:tableHeaders.lastRun')], ['successRate', t('workflows:tableHeaders.successRate')],
                  ['runtime', t('workflows:tableHeaders.runtime')], ['created', t('workflows:tableHeaders.created')], ['updated', t('workflows:tableHeaders.updated')],
                ] as [ResizableColKey, string][]).map(([col, label]) => (
                  <th key={col} style={{ width: colWidths[col] }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                    <button
                      onClick={() => handleSort(col)}
                      className="flex items-center gap-1 hover:text-on-surface transition-colors"
                    >
                      {label}
                      {sortBy === col
                        ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                        : <span className="w-3" />}
                    </button>
                    <div
                      onMouseDown={e => startResize(col, e)}
                      className="absolute right-0 top-0 h-full w-px cursor-col-resize bg-on-surface-variant/20 hover:bg-blue-400/70 active:bg-blue-500/80 transition-colors"
                    />
                  </th>
                ))}
                <th style={{ width: ACTIONS_WIDTH }} className="px-4 py-3 text-left">{t('workflows:tableHeaders.actions')}</th>
              </tr>
            </thead>
            <tbody ref={tbodyRef} className="divide-y divide-outline-variant/30">
              {/* Top spacer — fills the virtual area above the visible window. */}
              {(() => {
                const items = rowVirtualizer.getVirtualItems();
                const top = (items[0]?.start ?? 0) - scrollMargin;
                return top > 0 ? <tr><td colSpan={10} style={{ height: top }} /></tr> : null;
              })()}
              {rowVirtualizer.getVirtualItems().map((vRow) => {
                const w = sortedWorkflows[vRow.index];
                // Drag-and-drop: gate by canEdit so we don't dangle a draggable affordance
                // that the backend would 403 anyway. Server still authoritatively checks
                // Edit on both source AND destination folder.
                const canDrag = w.capabilities?.canEdit ?? canWrite;
                return (
                <tr
                  key={w.id}
                  data-index={vRow.index}
                  ref={rowVirtualizer.measureElement}
                  className={`hover:bg-surface-low ${canDrag ? 'cursor-grab active:cursor-grabbing' : 'cursor-pointer'}`}
                  draggable={canDrag}
                  onClick={(e) => {
                    if (e.target instanceof Element && e.target.closest('button, a, input, [role="button"]')) return;
                    navigate(`/workflows/${w.id}`);
                  }}
                  onDragStart={(e) => {
                    if (!canDrag) return;
                    e.dataTransfer.effectAllowed = 'move';
                    e.dataTransfer.setData(WORKFLOW_DRAG_MIME, w.id);
                    // Plain-text fallback is useful for accidental drops into editors / debug.
                    e.dataTransfer.setData('text/plain', w.name);
                  }}
                >
                  <td className="px-4 py-2 overflow-hidden">
                    <div className="flex items-center gap-2 min-w-0">
                      <button
                        onClick={() => navigate(`/workflows/${w.id}`)}
                        className="text-sm font-semibold text-on-surface-variant hover:text-primary transition-colors truncate text-left"
                      >
                        {w.name}
                      </button>
                      <span className="text-[10px] font-medium text-outline bg-surface-container rounded px-1.5 py-0.5 shrink-0">
                        v{w.version}
                      </span>
                    </div>
                    {w.description && (
                      <p className="text-xs text-outline mt-0.5 truncate" title={w.description}>
                        {w.description}
                      </p>
                    )}
                  </td>
                  <td className="px-4 py-2 overflow-hidden">
                    <div className="flex items-center gap-1.5 text-sm text-on-surface-variant">
                      <Apps size={14} className="text-outline" />
                      <span className="tabular-nums">{w.activityCount ?? 0}</span>
                    </div>
                  </td>
                  <td className="px-4 py-2 overflow-hidden">
                    {w.triggerTypes && w.triggerTypes.length > 0 ? (
                      <div className="flex flex-wrap gap-1">
                        {w.triggerTypes.map((t) => {
                          const meta = TRIGGER_META[t] ?? { label: t, icon: FlashFilled, className: 'bg-surface-container text-on-surface' };
                          const Icon = meta.icon;
                          return (
                            <span
                              key={t}
                              className={`inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[11px] font-medium ${meta.className}`}
                              title={t}
                            >
                              <Icon size={11} />
                              {meta.label}
                            </span>
                          );
                        })}
                      </div>
                    ) : (
                      <span className="text-xs text-outline">–</span>
                    )}
                  </td>
                  <td className="px-4 py-2 overflow-hidden">
                    {(() => {
                      // Status badge: 4 mutually exclusive states.
                      // Productive (enabled, no lock) → green. Disabled (no lock) → gray.
                      // Lock-by-me → blue. Lock-by-other → amber + owner name.
                      const lockedByMe = !!w.checkedOutByUserId && w.checkedOutByUserId === currentUserId;
                      const lockedByOther = !!w.checkedOutByUserId && !lockedByMe;
                      if (lockedByMe) {
                        return (
                          <span className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium bg-blue-500/15 text-blue-600 dark:text-blue-400" title={t('workflows:statusEditingTitle')}>
                            <Edit size={11} /> {t('workflows:statusEditing')}
                          </span>
                        );
                      }
                      if (lockedByOther) {
                        return (
                          <span className="inline-flex shrink-0 items-center gap-1 whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium bg-amber-500/15 text-amber-600 dark:text-amber-400"
                                title={t('workflows:statusLockedByTitle', { user: w.checkedOutByUserName ?? t('common:unknown'), time: w.checkedOutAt ? formatDate(w.checkedOutAt) : t('common:unknown') })}>
                            <Locked size={11} className="shrink-0" /> <span className="max-w-[110px] truncate">{w.checkedOutByUserName ?? t('workflows:statusLocked')}</span>
                          </span>
                        );
                      }
                      return (
                        <span className={`inline-flex shrink-0 items-center whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-medium ${w.isEnabled ? 'bg-green-500/15 text-green-600 dark:text-green-400' : 'bg-surface-container text-on-surface-variant'}`}>
                          {w.isEnabled ? t('workflows:statusProductive') : t('workflows:statusDisabled')}
                        </span>
                      );
                    })()}
                  </td>
                  <td className="px-4 py-2 overflow-hidden">
                    <LastRunCell info={w.lastExecution} />
                  </td>
                  <td className="px-4 py-2 overflow-hidden">
                    <SuccessRateCell success={w.successCount ?? 0} total={w.totalCount ?? 0} />
                  </td>
                  <td className="px-4 py-2 overflow-hidden text-sm text-on-surface-variant tabular-nums">
                    {formatDuration(w.avgDurationMs)}
                  </td>
                  <td className="px-4 py-2 overflow-hidden">
                    <div className="text-xs text-outline">{formatDate(w.createdAt)}</div>
                    {w.createdBy && (
                      <div className="text-[11px] text-on-surface-variant/60 mt-0.5 truncate">{t('common:by')} {w.createdBy}</div>
                    )}
                  </td>
                  <td className="px-4 py-2 overflow-hidden">
                    <div className="text-xs text-outline">{formatDate(w.updatedAt)}</div>
                    {w.updatedBy && (
                      <div className="text-[11px] text-on-surface-variant/60 mt-0.5 truncate">{t('common:by')} {w.updatedBy}</div>
                    )}
                  </td>
                  <td className="px-4 py-3 text-right overflow-hidden">
                    <div className="flex flex-nowrap items-center justify-end gap-1 whitespace-nowrap">
                      {(() => {
                        const lockedByMe = !!w.checkedOutByUserId && w.checkedOutByUserId === currentUserId;
                        const lockedByOther = !!w.checkedOutByUserId && !lockedByMe;
                        return (
                          <>
                            {rowCanRun(w) && w.isEnabled && !w.checkedOutByUserId && (
                              <button
                                onClick={() => handleRunWorkflow(w)}
                                className="p-1.5 text-green-600 hover:bg-green-500/15 rounded-lg"
                                title={t('workflows:actionRun')}
                              >
                                <Play size={16} />
                              </button>
                            )}
                            {rowCanEdit(w) && !lockedByOther && (
                              <button
                                onClick={() => w.isEnabled
                                  ? disableMutation.mutate(w.id)
                                  : enableMutation.mutate(w.id)}
                                disabled={enableMutation.isPending || disableMutation.isPending || lockedByMe}
                                title={lockedByMe
                                  ? t('workflows:actionEnableLockedHint')
                                  : (w.isEnabled ? t('workflows:actionDisable') : t('workflows:actionEnable'))}
                                className={`p-1.5 rounded-lg transition-colors disabled:opacity-40 ${
                                  w.isEnabled
                                    ? 'text-green-500 hover:text-red-500 hover:bg-red-500/15'
                                    : 'text-gray-400 hover:text-green-500 hover:bg-green-500/15'
                                }`}
                              >
                                <Power size={15} />
                              </button>
                            )}
                            {rowCanForceUnlock(w) && lockedByOther && (
                              <button
                                onClick={async () => {
                                  if (await confirmDialog(t('workflows:forceUnlockConfirm', { name: w.name, user: w.checkedOutByUserName ?? t('common:unknown') }))) {
                                    forceUnlockMutation.mutate(w.id);
                                  }
                                }}
                                disabled={forceUnlockMutation.isPending}
                                className="p-1.5 text-amber-600 hover:bg-amber-500/15 rounded-lg disabled:opacity-40"
                                title={t('workflows:actionForceUnlock')}
                              >
                                <Unlocked size={15} />
                              </button>
                            )}
                          </>
                        );
                      })()}
                      <button
                        onClick={() => navigate(`/workflows/${w.id}`)}
                        className="p-1.5 text-blue-600 hover:bg-blue-50 rounded-lg"
                        title={rowCanEdit(w) ? t('workflows:actionEdit') : t('workflows:actionView')}
                      >
                        <Edit size={16} />
                      </button>
                      {rowCanEdit(w) && (
                        <button
                          onClick={() => duplicateMutation.mutate(w.id)}
                          className="p-1.5 text-indigo-600 hover:bg-indigo-500/15 rounded-lg"
                          title={t('workflows:actionDuplicate')}
                        >
                          <Copy size={16} />
                        </button>
                      )}
                      <button
                        onClick={() => handleExportOne(w)}
                        className="p-1.5 text-on-surface-variant hover:bg-surface-container rounded-lg"
                        title={t('workflows:actionExport')}
                      >
                        <Download size={16} />
                      </button>
                      {rowCanDelete(w) && (
                        <button
                          onClick={async () => {
                            if (await confirmDialog({ message: t('workflows:deleteConfirm'), danger: true }))
                              deleteMutation.mutate(w.id);
                          }}
                          className="p-1.5 text-red-600 hover:bg-red-500/15 rounded-lg"
                          title={t('workflows:actionDelete')}
                        >
                          <TrashCan size={16} />
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
                );
              })}
              {/* Bottom spacer — fills the virtual area below the visible window. */}
              {(() => {
                const items = rowVirtualizer.getVirtualItems();
                const bottom = rowVirtualizer.getTotalSize() - (items.at(-1)?.end ?? 0);
                return bottom > 0 ? <tr><td colSpan={10} style={{ height: bottom }} /></tr> : null;
              })()}
            </tbody>
          </table>
          </div>
        </div>
      )}
      </div>
      </div>

      {/* Folder permissions modal. Mounted at the page root so its overlay escapes
          the sidebar layout. Loads grants for the chosen folder; admin-only. */}
      {permissionsModalFolderId && (
        <SharedFolderPermissionsModal
          folderId={permissionsModalFolderId}
          folderPath={
            sharedFolders?.find((f: SharedFolder) => f.id === permissionsModalFolderId)?.path
            ?? workflows?.find(w => w.folderId === permissionsModalFolderId)?.folderPath
            ?? (permissionsModalFolderId === ROOT_FOLDER_ID ? '/' : '')
          }
          users={usersForPicker ?? []}
          onClose={() => {
            setPermissionsModalFolderId(null);
            queryClient.invalidateQueries({ queryKey: ['workflows'] });
            queryClient.invalidateQueries({ queryKey: ['shared-folders'] });
          }}
        />
      )}

      {/* AI workflow generation — prompt + preview + confirm, all in one dialog. */}
      {showAiGenerate && (
        <WorkflowGenerationDialog
          onClose={() => setShowAiGenerate(false)}
          onCreate={(req) => aiCreateMutation.mutateAsync(req).then(() => undefined)}
        />
      )}

      {/* SCOrch Import Result Modal — richer than native import because translation
          rarely round-trips 1:1 and the operator needs to see every warning. */}
      {scorchResult && (
        <ScorchImportResultModal
          result={scorchResult.resp}
          filename={scorchResult.filename}
          onClose={() => setScorchResult(null)}
          onOpenWorkflow={(id) => { setScorchResult(null); navigate(`/workflows/${id}`); }}
        />
      )}

      {/* Run Dialog for ManualTrigger workflows */}
      {runDialogWorkflow && (() => {
        const triggerConfig = extractManualTriggerConfig(runDialogWorkflow.definitionJson);
        if (!triggerConfig) return null;
        return (
          <RunWorkflowDialog
            workflowName={runDialogWorkflow.name}
            triggerTitle={triggerConfig.title}
            triggerDescription={triggerConfig.description}
            parameters={triggerConfig.parameters}
            onExecute={(params) => {
              executeMutation.mutate({ id: runDialogWorkflow.id, params });
              setRunDialogWorkflow(null);
            }}
            onCancel={() => setRunDialogWorkflow(null)}
          />
        );
      })()}
    </div>
  );
}

/**
 * Post-import review modal for SCOrch runbook imports. Surfaces the per-runbook fidelity
 * counts (heuristic vs. placeholder mappings) + every warning so the operator sees which
 * steps need manual touch-up before enabling the workflow.
 */
function ScorchImportResultModal({
  result, filename, onClose, onOpenWorkflow,
}: {
  result: ScorchImportResponse;
  filename: string;
  onClose: () => void;
  onOpenWorkflow: (id: string) => void;
}) {
  const { t } = useTranslation(['workflows', 'common']);
  const totalActivities = result.workflows.reduce((acc, w) => acc + w.activityCount, 0);
  const totalHeuristics = result.workflows.reduce((acc, w) => acc + w.heuristicCount, 0);
  const totalFallbacks  = result.workflows.reduce((acc, w) => acc + w.fallbackCount, 0);
  const variablesImported = result.variables.filter(v => v.createdNow).length;
  const variablesSkipped = result.variables.filter(v => v.skipped).length;

  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
      <div className="bg-surface-lowest rounded-lg shadow-xl max-w-3xl w-full max-h-[85vh] flex flex-col">
        <div className="px-5 py-4 border-b">
          <h3 className="text-lg font-semibold text-on-surface">
            {t('workflows:scorch.importedTitle', { count: result.created })}
            {variablesImported > 0 && t('workflows:scorch.andVariables', { count: variablesImported })}
            {t('workflows:scorch.fromFile', { file: filename })}
          </h3>
          {/* Same pattern as UsersPage: use Trans + a components map instead of
              dangerouslySetInnerHTML, so a <strong> tag in the translation string renders
              as a real React element instead of being injected as raw HTML. */}
          <p className="text-xs text-on-surface-variant mt-1">
            <Trans i18nKey="workflows:scorch.warningHint" components={{ strong: <strong /> }} />
          </p>
        </div>

        <div className="px-5 py-3 border-b bg-surface-low flex items-center gap-4 text-xs">
          <Metric label={t('workflows:scorch.metricActivities')} value={totalActivities} />
          <Metric label={t('workflows:scorch.metricHeuristic')} value={totalHeuristics} tone={totalHeuristics > 0 ? 'amber' : undefined}
                  title={t('workflows:scorch.metricHeuristicTitle')} />
          <Metric label={t('workflows:scorch.metricPlaceholders')} value={totalFallbacks} tone={totalFallbacks > 0 ? 'red' : undefined}
                  title={t('workflows:scorch.metricPlaceholdersTitle')} />
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-3 space-y-4">
          {/* Imported workflows list */}
          {result.workflows.length > 0 && (
            <section>
              <h4 className="text-xs font-semibold text-on-surface-variant uppercase tracking-wide mb-2">
                {t('workflows:scorch.importedWorkflows')}
              </h4>
              <ul className="space-y-1">
                {result.workflows.map((w) => (
                  <li key={w.id} className="flex items-center justify-between p-2 rounded border border-outline-variant/50 hover:bg-surface-low">
                    <div className="flex items-center gap-2 min-w-0">
                      <button
                        onClick={() => onOpenWorkflow(w.id)}
                        className="text-sm font-semibold text-on-surface-variant hover:text-primary transition-colors truncate text-left"
                      >
                        {w.name}
                      </button>
                      {w.originalName && (
                        <span className="text-xs text-outline" title={`Original: ${w.originalName}`}>
                          {t('workflows:scorch.originalWas', { name: w.originalName })}
                        </span>
                      )}
                    </div>
                    <div className="flex items-center gap-2 text-[11px] tabular-nums shrink-0">
                      <span className="text-on-surface-variant">{t('workflows:scorch.stepsCount', { count: w.activityCount })}</span>
                      {w.heuristicCount > 0 && (
                        <span className="text-amber-600">{t('workflows:scorch.heuristicCount', { count: w.heuristicCount })}</span>
                      )}
                      {w.fallbackCount > 0 && (
                        <span className="text-red-600">{t('workflows:scorch.placeholderCount', { count: w.fallbackCount })}</span>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {/* Imported variables */}
          {result.variables.length > 0 && (
            <section>
              <h4 className="text-xs font-semibold text-on-surface-variant uppercase tracking-wide mb-2">
                {t('workflows:scorch.globalVariables', { imported: variablesImported, skipped: variablesSkipped })}
              </h4>
              <ul className="space-y-1">
                {result.variables.map((v, i) => (
                  <li key={i} className="flex items-center justify-between p-2 rounded border border-outline-variant/50">
                    <div className="flex items-center gap-2 min-w-0">
                      <span className="text-sm font-mono text-on-surface truncate">{v.name}</span>
                      {v.originalName && (
                        <span className="text-xs text-outline">{t('workflows:scorch.varOriginalWas', { name: v.originalName })}</span>
                      )}
                    </div>
                    <div className="text-xs shrink-0">
                      {v.createdNow && <span className="text-green-600">{t('workflows:scorch.varCreated')}</span>}
                      {v.skipped && (
                        <span className="text-amber-600" title={v.skipReason ?? undefined}>
                          {t('workflows:scorch.varSkipped')}
                        </span>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {/* Warnings */}
          {result.warnings.length > 0 && (
            <section>
              <h4 className="text-xs font-semibold text-amber-700 uppercase tracking-wide mb-2">
                {t('workflows:scorch.warnings', { count: result.warnings.length })}
              </h4>
              <ul className="space-y-1 text-xs font-mono text-amber-900 bg-amber-50 border border-amber-200 rounded p-2 max-h-64 overflow-y-auto">
                {result.warnings.map((w, i) => <li key={i}>• {w}</li>)}
              </ul>
            </section>
          )}

          {/* Errors */}
          {result.errors.length > 0 && (
            <section>
              <h4 className="text-xs font-semibold text-red-700 uppercase tracking-wide mb-2">
                {t('workflows:scorch.errors', { count: result.errors.length })}
              </h4>
              <ul className="space-y-1 text-xs font-mono text-red-900 bg-red-50 border border-red-200 rounded p-2 max-h-64 overflow-y-auto">
                {result.errors.map((e, i) => <li key={i}>• {e}</li>)}
              </ul>
            </section>
          )}
        </div>

        <div className="px-5 py-3 border-t bg-surface-low flex justify-end">
          <button
            onClick={onClose}
            className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm"
          >
            {t('common:close')}
          </button>
        </div>
      </div>
    </div>
  );
}

function Metric({ label, value, tone, title }: { label: string; value: number; tone?: 'amber' | 'red'; title?: string }) {
  const color = tone === 'red' ? 'text-red-600' : tone === 'amber' ? 'text-amber-600' : 'text-on-surface';
  return (
    <span title={title} className="inline-flex items-baseline gap-1.5">
      <span className={`font-semibold tabular-nums ${color}`}>{value}</span>
      <span className="text-on-surface-variant">{label}</span>
    </span>
  );
}
