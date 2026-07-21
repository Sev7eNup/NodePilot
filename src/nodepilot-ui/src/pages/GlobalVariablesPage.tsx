import { Add, ChevronDown, ChevronUp, Edit, Locked, Search, TrashCan, Unlocked } from '@carbon/icons-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { ModalShell } from '../components/common/ModalShell';
import { MobileCardList } from '../components/common/MobileCardList';
import { useRole } from '../lib/rbac';
import { useIsMobile } from '../hooks/useMediaQuery';
import { useResizable } from '../hooks/useResizable';
import { formatDate } from '../lib/format';
import { toast } from '../stores/toastStore';
import { confirmDialog } from '../stores/confirmStore';
import { GlobalFolderTree } from '../components/globals/GlobalFolderTree';
import { globalFoldersApi, ROOT_FOLDER_ID, GLOBAL_VARIABLE_DRAG_MIME, type GlobalFolder } from '../api/globalFolders';
import { ResizeHandle, CornerResizeHandle } from '../components/designer/library/NodeLibrary';

/**
 * Admin-managed constants available to every workflow via `{{globals.NAME}}` templates.
 * Secrets are stored DPAPI-encrypted on the server and never returned (masked as `"***"`);
 * non-secrets are shown and editable inline. Think SCOrch Variables.
 *
 * Variables are organized into a folder tree (left sidebar) purely for navigation — a folder
 * never changes how a variable resolves (names stay globally unique). Selecting a folder scopes
 * the list to that folder and its descendants; Root shows everything.
 *
 * Access: Admin/Operator can list; only Admin can mutate.
 */
type GlobalVariable = {
  id: string;
  name: string;
  value: string | null;
  isSecret: boolean;
  description: string | null;
  folderId: string;
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
};

type FormState = {
  id: string | null;
  name: string;
  value: string;
  isSecret: boolean;
  description: string;
  folderId: string;
  valueTouched: boolean;
};

const emptyForm = (folderId: string): FormState => ({
  id: null, name: '', value: '', isSecret: false, description: '', folderId, valueTouched: false,
});

// Mirrors MachinesPage: ColKey covers every sortable column; ResizableColKey
// drops the auto-flex column (description) which has no explicit width and no
// drag-handle. Value is sortable-excluded — masked "***" strings sort to noise.
type ColKey = 'name' | 'type' | 'value' | 'description' | 'updated';
type ResizableColKey = Exclude<ColKey, 'description'>;

const ACTIONS_WIDTH = 90; // 2 buttons × ~28px + gap-1 + px-4 cell padding
const DESCRIPTION_MIN_WIDTH = 220;
const DEFAULT_WIDTHS: Record<ResizableColKey, number> = {
  name: 220, type: 130, value: 240, updated: 160,
};

export function GlobalVariablesPage() {
  const { t } = useTranslation(['globals', 'common']);
  const queryClient = useQueryClient();
  const { canAdmin } = useRole();
  const isMobile = useIsMobile();
  // Selected folder scopes the list (descendant-inclusive). Default = Root → shows everything.
  const [selectedFolderId, setSelectedFolderId] = useState<string>(ROOT_FOLDER_ID);
  const [form, setForm] = useState<FormState>(() => emptyForm(ROOT_FOLDER_ID));
  const [showDialog, setShowDialog] = useState(false);

  const [search, setSearch] = useState('');
  const [sortBy, setSortBy] = useState<ColKey | null>('name');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('asc');

  // Drag-resizable folder sidebar (same primitives as WorkflowsPage). Width is pixel-sized;
  // height defaults to auto (fits the tree) until the corner grip is dragged.
  const folderPanel = useResizable({ initialSize: 256, minSize: 180, maxSize: 600, direction: 'horizontal' });
  // Height floor kept below the compact tree's natural content height (header + a couple of
  // rows ≈ 90px). A larger floor than the content means grabbing the grip clamps the box UP
  // to the floor on the first move — a visible downward "jump" — and blocks shrinking flush
  // to the entries. The globals tree is denser than the workflow one, so it needs a lower
  // floor than that page's 160 to shrink all the way up. Double-click still resets to auto.
  const folderPanelHeight = useResizable({ initialSize: 360, minSize: 72, maxSize: 800, direction: 'vertical' });
  const [folderHeightDirty, setFolderHeightDirty] = useState(false);
  const folderBoxRef = useRef<HTMLDivElement>(null);

  // Column resizing (same pattern as MachinesPage / WorkflowsPage). Description
  // is excluded — it's the auto-flex column, so it has no inline width and no
  // drag-handle; it absorbs leftover horizontal space.
  const [colWidths, setColWidths] = useState(DEFAULT_WIDTHS);
  const tableMinWidth = useMemo(
    () => Object.values(colWidths).reduce((a, b) => a + b, 0) + ACTIONS_WIDTH + DESCRIPTION_MIN_WIDTH,
    [colWidths],
  );
  const resizeRef = useRef<{ col: ResizableColKey; startX: number; startWidth: number } | null>(null);

  const startResize = (col: ResizableColKey, e: React.MouseEvent) => {
    e.preventDefault();
    resizeRef.current = { col, startX: e.clientX, startWidth: colWidths[col] };
    const onMove = (ev: MouseEvent) => {
      if (!resizeRef.current) return;
      const { col, startWidth, startX } = resizeRef.current;
      const w = Math.max(50, startWidth + ev.clientX - startX);
      setColWidths((prev) => ({ ...prev, [col]: w }));
    };
    const onUp = () => {
      resizeRef.current = null;
      globalThis.removeEventListener('mousemove', onMove);
      globalThis.removeEventListener('mouseup', onUp);
    };
    globalThis.addEventListener('mousemove', onMove);
    globalThis.addEventListener('mouseup', onUp);
  };

  const handleSort = (col: ColKey) => {
    if (sortBy === col) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    else { setSortBy(col); setSortDir('asc'); }
  };

  const { data: variables, isLoading } = useQuery({
    queryKey: ['global-variables'],
    queryFn: () => api.get<GlobalVariable[]>('/global-variables'),
  });
  const { data: folders } = useQuery({
    queryKey: ['global-folders'],
    queryFn: () => globalFoldersApi.list(),
  });

  const saveMutation = useMutation({
    mutationFn: async (f: FormState) => {
      if (f.id) {
        // For secret variables where the value wasn't re-entered, send null so the server
        // keeps the existing ciphertext. Any other case ships the current value.
        const body = {
          name: f.name.trim(),
          value: f.isSecret && !f.valueTouched ? null : f.value,
          isSecret: f.isSecret,
          description: f.description.trim() || null,
          folderId: f.folderId,
        };
        await api.put(`/global-variables/${f.id}`, body);
      } else {
        await api.post('/global-variables', {
          name: f.name.trim(),
          value: f.value,
          isSecret: f.isSecret,
          description: f.description.trim() || null,
          folderId: f.folderId,
        });
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['global-variables'] });
      queryClient.invalidateQueries({ queryKey: ['global-folders'] });
      setShowDialog(false);
    },
    onError: (err: Error) => toast.error(t('common:saveFailed', { message: err.message })),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.delete(`/global-variables/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['global-variables'] });
      queryClient.invalidateQueries({ queryKey: ['global-folders'] });
    },
  });

  const moveMutation = useMutation({
    mutationFn: ({ id, folderId }: { id: string; folderId: string }) =>
      globalFoldersApi.moveVariableToFolder(id, folderId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['global-variables'] });
      queryClient.invalidateQueries({ queryKey: ['global-folders'] });
      toast.success(t('globals:folder.moved'));
    },
    onError: (err: Error) => toast.error(t('common:saveFailed', { message: err.message })),
  });

  const openCreate = () => { setForm(emptyForm(selectedFolderId)); setShowDialog(true); };
  const openEdit = (v: GlobalVariable) => {
    setForm({
      id: v.id,
      name: v.name,
      value: v.isSecret ? '' : (v.value ?? ''),
      isSecret: v.isSecret,
      description: v.description ?? '',
      folderId: v.folderId,
      valueTouched: false,
    });
    setShowDialog(true);
  };

  // Descendant-inclusive set of the selected folder (+ itself). Root → every folder id, so all
  // variables show. Falls back to just the selected id until the folder list has loaded.
  const scopedFolderIds = useMemo(() => {
    const list = folders ?? [];
    const childrenByParent = new Map<string, string[]>();
    for (const f of list) {
      if (f.parentFolderId) {
        const arr = childrenByParent.get(f.parentFolderId) ?? [];
        arr.push(f.id);
        childrenByParent.set(f.parentFolderId, arr);
      }
    }
    const set = new Set<string>();
    const stack = [selectedFolderId];
    while (stack.length) {
      const id = stack.pop()!;
      if (set.has(id)) continue;
      set.add(id);
      for (const c of childrenByParent.get(id) ?? []) stack.push(c);
    }
    return set;
  }, [folders, selectedFolderId]);

  // Folder options for the create/edit dropdown, sorted by path (Root first).
  const folderOptions = useMemo(() => {
    const list = [...(folders ?? [])].sort((a, b) => a.path.localeCompare(b.path));
    return list.map((f: GlobalFolder) => ({
      id: f.id,
      label: f.id === ROOT_FOLDER_ID ? t('globals:folder.allRoot') : f.path,
    }));
  }, [folders, t]);

  const filteredSorted = useMemo(() => {
    let list = (variables ?? []).filter((v) => scopedFolderIds.has(v.folderId));
    const term = search.trim().toLowerCase();
    if (term) {
      list = list.filter((v) =>
        v.name.toLowerCase().includes(term)
        || (v.description ?? '').toLowerCase().includes(term)
        // Don't leak secret values through search — only match plain values.
        || (!v.isSecret && (v.value ?? '').toLowerCase().includes(term)),
      );
    }
    if (!sortBy) return list;
    return [...list].sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'name':        cmp = a.name.localeCompare(b.name); break;
        case 'type':        cmp = Number(a.isSecret) - Number(b.isSecret); break;
        case 'value':       cmp = (a.value ?? '').localeCompare(b.value ?? ''); break;
        case 'description': cmp = (a.description ?? '').localeCompare(b.description ?? ''); break;
        case 'updated':     cmp = a.updatedAt.localeCompare(b.updatedAt); break;
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
  }, [variables, scopedFolderIds, search, sortBy, sortDir]);

  const totalCount = variables?.length ?? 0;
  // Scoped count (in the selected folder subtree) drives the empty-state messaging: distinguish
  // "no globals at all" from "none in this folder".
  const scopedCount = useMemo(
    () => (variables ?? []).filter((v) => scopedFolderIds.has(v.folderId)).length,
    [variables, scopedFolderIds],
  );

  return (
    <div className="max-w-[1600px] mx-auto np-fade-up">
      <div className="flex items-center justify-between mb-6">
        <div>
          <p className="text-sm text-on-surface-variant mt-1">
            {t('globals:subtitle').split('<code>')[0]}
            <code className="text-xs bg-surface-container px-1.5 py-0.5 rounded">{'{{globals.NAME}}'}</code>
            {t('globals:subtitle').split('</code>')[1] ?? ''}
          </p>
        </div>
        {canAdmin && (
          <button
            onClick={openCreate}
            title={t('globals:newVariable')}
            className="flex items-center gap-2 px-3 py-2 sm:px-4 bg-blue-600 text-white rounded-md hover:bg-blue-700 text-sm"
          >
            <Add size={16} /> <span className="hidden sm:inline">{t('globals:newVariable')}</span>
          </button>
        )}
      </div>
      {/* Toolbar: full-width search box. Hidden when there are no variables at all. */}
      {totalCount > 0 && (
        <div className="np-card p-3 mb-3 flex flex-wrap items-center gap-3">
          <div className="relative w-full sm:flex-1 sm:min-w-[220px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-outline" />
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('globals:searchPlaceholder')}
              className="w-full pl-8 pr-3 py-1.5 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
        </div>
      )}
      <div className="flex flex-col lg:flex-row">
        {/* Folder sidebar — desktop: resizable left rail; mobile: collapsible disclosure. */}
        {(() => {
          const folderTree = (
            <GlobalFolderTree
              selectedFolderId={selectedFolderId}
              onFolderSelected={(id) => setSelectedFolderId(id ?? ROOT_FOLDER_ID)}
              canManage={canAdmin}
              onTreeMutated={() => queryClient.invalidateQueries({ queryKey: ['global-variables'] })}
              onVariableDropped={(variableId, folderId) => {
                const v = variables?.find((x) => x.id === variableId);
                if (v && v.folderId === folderId) return; // no-op move
                moveMutation.mutate({ id: variableId, folderId });
              }}
            />
          );
          if (isMobile) {
            return (
              <details className="np-card p-3 mb-3">
                <summary className="cursor-pointer select-none text-sm font-medium text-on-surface-variant">
                  {t('globals:folder.heading')}
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
                  height: folderHeightDirty ? folderPanelHeight.size : undefined,
                  maxHeight: 'calc(100vh - 3rem)',
                }}
              >
                <aside className="np-card np-folder-card p-0 h-full w-full overflow-hidden">
                  {folderTree}
                </aside>
                <CornerResizeHandle
                  title={t('globals:folder.heading')}
                  onMouseDown={(e) => {
                    const startH = folderBoxRef.current?.getBoundingClientRect().height;
                    folderPanel.handleProps.onMouseDown(e);
                    folderPanelHeight.handleProps.onMouseDown(e, startH);
                    setFolderHeightDirty(true);
                    document.body.style.cursor = 'nwse-resize';
                  }}
                  onDoubleClick={() => {
                    folderPanel.handleProps.onDoubleClick();
                    setFolderHeightDirty(false);
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
          ) : totalCount === 0 ? (
            <div className="np-card p-8 text-center text-outline">
              {t('globals:noVariables')}
            </div>
          ) : scopedCount === 0 ? (
            <div className="np-card p-8 text-center text-outline">
              {t('globals:folder.noVariablesInFolder')}
            </div>
          ) : filteredSorted.length === 0 ? (
            <div className="np-card p-8 text-center text-outline">
              {t('globals:noMatch')}
            </div>
          ) : isMobile ? (
            <MobileCardList
              items={filteredSorted}
              getKey={(v) => v.id}
              renderTitle={(v) => (
                <code className="text-sm font-mono font-semibold text-on-surface truncate block" title={v.name}>{v.name}</code>
              )}
              renderFields={(v) => [
                {
                  label: t('globals:tableHeaders.type'),
                  value: v.isSecret ? (
                    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-medium bg-amber-500/15 text-amber-600 dark:text-amber-400">
                      <Locked size={11} /> {t('globals:typeSecret')}
                    </span>
                  ) : (
                    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-medium bg-surface-container text-on-surface-variant">
                      <Unlocked size={11} /> {t('globals:typePlain')}
                    </span>
                  ),
                },
                {
                  label: t('globals:tableHeaders.value'),
                  value: <code className="text-xs text-on-surface-variant font-mono break-all">{v.isSecret ? '***' : (v.value ?? '')}</code>,
                },
                {
                  label: t('globals:tableHeaders.description'),
                  value: <span className="text-sm text-on-surface-variant">{v.description ?? t('common:dash')}</span>,
                },
                {
                  label: t('globals:tableHeaders.updated'),
                  value: <span className="text-xs text-on-surface-variant" title={formatDate(v.updatedAt)}>{v.updatedBy ?? t('common:dash')}</span>,
                },
              ]}
              renderActions={canAdmin ? (v) => (
                <>
                  <button onClick={() => openEdit(v)} className="p-2 text-blue-600 hover:bg-blue-50 rounded-lg" title={t('common:edit')}>
                    <Edit size={16} />
                  </button>
                  <button
                    onClick={async () => { if (await confirmDialog({ message: t('globals:deleteConfirm', { name: v.name }), danger: true })) deleteMutation.mutate(v.id); }}
                    className="p-2 text-red-600 hover:bg-red-500/15 rounded-lg"
                    title={t('common:delete')}
                  >
                    <TrashCan size={16} />
                  </button>
                </>
              ) : undefined}
            />
          ) : (
            <div className="np-card overflow-hidden"><div className="overflow-x-auto">
              <table
                style={{
                  tableLayout: 'fixed',
                  width: '100%',
                  minWidth: tableMinWidth,
                }}
              >
                <thead className="np-col-header text-left text-xs font-semibold uppercase tracking-wide text-on-surface-variant">
                  <tr>
                    {/* Fixed-width sortable + resizable columns. Description (rendered
                        after this loop) is the auto-flex column — it has no inline
                        width, no resize handle, and absorbs leftover horizontal space. */}
                    {([
                      ['name', t('globals:tableHeaders.name')],
                      ['type', t('globals:tableHeaders.type')],
                      ['value', t('globals:tableHeaders.value')],
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
                          onMouseDown={(e) => startResize(col, e)}
                          className="absolute right-0 top-0 h-full w-px cursor-col-resize bg-on-surface-variant/20 hover:bg-blue-400/70 active:bg-blue-500/80 transition-colors"
                        />
                      </th>
                    ))}
                    {/* Description = auto-flex. No explicit width, no resize handle —
                        it absorbs whatever horizontal space the fixed columns leave. */}
                    <th style={{ minWidth: DESCRIPTION_MIN_WIDTH }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                      <button
                        onClick={() => handleSort('description')}
                        className="flex items-center gap-1 hover:text-on-surface transition-colors"
                      >
                        {t('globals:tableHeaders.description')}
                        {sortBy === 'description'
                          ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                          : <span className="w-3" />}
                      </button>
                    </th>
                    <th style={{ width: colWidths.updated }} className="relative px-4 py-2 whitespace-nowrap overflow-hidden">
                      <button
                        onClick={() => handleSort('updated')}
                        className="flex items-center gap-1 hover:text-on-surface transition-colors"
                      >
                        {t('globals:tableHeaders.updated')}
                        {sortBy === 'updated'
                          ? (sortDir === 'asc' ? <ChevronUp size={12} /> : <ChevronDown size={12} />)
                          : <span className="w-3" />}
                      </button>
                      <div
                        onMouseDown={(e) => startResize('updated', e)}
                        className="absolute right-0 top-0 h-full w-px cursor-col-resize bg-on-surface-variant/20 hover:bg-blue-400/70 active:bg-blue-500/80 transition-colors"
                      />
                    </th>
                    <th style={{ width: ACTIONS_WIDTH }} className="px-4 py-2 text-left">{t('globals:tableHeaders.actions')}</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-outline-variant/30">
                  {filteredSorted.map((v) => (
                    <tr
                      key={v.id}
                      className="hover:bg-surface-low"
                      // Drag a variable row onto a folder in the sidebar to move it (Admin only).
                      draggable={canAdmin}
                      onDragStart={canAdmin ? (e) => {
                        e.dataTransfer.setData(GLOBAL_VARIABLE_DRAG_MIME, v.id);
                        e.dataTransfer.effectAllowed = 'move';
                      } : undefined}
                    >
                      <td className="px-4 py-2 overflow-hidden">
                        <code className="text-sm font-mono font-semibold text-on-surface-variant truncate block" title={v.name}>
                          {v.name}
                        </code>
                      </td>
                      <td className="px-4 py-2 overflow-hidden">
                        {v.isSecret ? (
                          <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-medium bg-amber-500/15 text-amber-600 dark:text-amber-400">
                            <Locked size={11} /> {t('globals:typeSecret')}
                          </span>
                        ) : (
                          <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-medium bg-surface-container text-on-surface-variant">
                            <Unlocked size={11} /> {t('globals:typePlain')}
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-2 overflow-hidden">
                        <code className="text-xs text-on-surface-variant font-mono truncate block" title={v.isSecret ? '' : (v.value ?? '')}>
                          {v.isSecret ? '***' : (v.value ?? '')}
                        </code>
                      </td>
                      <td className="px-4 py-2 overflow-hidden">
                        <span className="text-sm text-on-surface-variant truncate block" title={v.description ?? ''}>
                          {v.description ?? t('common:dash')}
                        </span>
                      </td>
                      <td className="px-4 py-2 overflow-hidden">
                        <span className="text-xs text-on-surface-variant truncate block" title={formatDate(v.updatedAt)}>
                          {v.updatedBy ?? t('common:dash')}
                        </span>
                      </td>
                      <td className="px-4 py-2 overflow-hidden">
                        <div className="flex items-center gap-1 whitespace-nowrap">
                          {canAdmin && (
                            <>
                              <button
                                onClick={() => openEdit(v)}
                                className="p-1.5 text-blue-600 hover:bg-blue-50 rounded-lg"
                                title={t('common:edit')}
                              >
                                <Edit size={16} />
                              </button>
                              <button
                                onClick={async () => {
                                  if (await confirmDialog({ message: t('globals:deleteConfirm', { name: v.name }), danger: true }))
                                    deleteMutation.mutate(v.id);
                                }}
                                className="p-1.5 text-red-600 hover:bg-red-500/15 rounded-lg"
                                title={t('common:delete')}
                              >
                                <TrashCan size={16} />
                              </button>
                            </>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div></div>
          )}
        </div>
      </div>
      {showDialog && (
        <ModalShell onClose={() => setShowDialog(false)} maxWidth="max-w-md">
            <h3 className="text-lg font-semibold mb-4 text-on-surface">
              {form.id ? t('globals:editTitle') : t('globals:createTitle')}
            </h3>

            <div className="space-y-3">
              <div>
                <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('globals:fields.name')}</label>
                <input
                  type="text"
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  placeholder="MY_CONSTANT"
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500"
                  pattern="[A-Za-z0-9_\-]+"
                />
                <p className="text-[11px] text-outline mt-0.5">
                  {t('globals:namePattern')}
                </p>
              </div>

              <div>
                <label className="block text-xs font-medium text-on-surface-variant mb-1">
                  {t('globals:fields.value')} {form.id && form.isSecret && !form.valueTouched && <span className="text-outline">{t('globals:valueKeepHint')}</span>}
                </label>
                <input
                  type={form.isSecret ? 'password' : 'text'}
                  value={form.value}
                  onChange={(e) => setForm({ ...form, value: e.target.value, valueTouched: true })}
                  placeholder={form.id && form.isSecret ? '••••••••' : ''}
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500"
                  autoComplete="off"
                />
              </div>

              <label className="flex items-center gap-2 text-sm text-on-surface cursor-pointer">
                <input
                  type="checkbox"
                  checked={form.isSecret}
                  onChange={(e) => setForm({ ...form, isSecret: e.target.checked })}
                  className="rounded"
                />
                {t('globals:fields.isSecret')}
              </label>

              <div>
                <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('globals:folder.field')}</label>
                <select
                  value={form.folderId}
                  onChange={(e) => setForm({ ...form, folderId: e.target.value })}
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm bg-surface-lowest focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  {folderOptions.map((o) => (
                    <option key={o.id} value={o.id}>{o.label}</option>
                  ))}
                </select>
              </div>

              <div>
                <label className="block text-xs font-medium text-on-surface-variant mb-1">{t('globals:fields.description')}</label>
                <textarea
                  value={form.description}
                  onChange={(e) => setForm({ ...form, description: e.target.value })}
                  rows={2}
                  className="w-full px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
            </div>

            <div className="flex justify-end gap-2 mt-5">
              <button
                onClick={() => { setShowDialog(false); }}
                className="px-4 py-2 text-sm text-on-surface-variant hover:bg-surface-container rounded-md"
              >
                {t('common:cancel')}
              </button>
              <button
                onClick={() => saveMutation.mutate(form)}
                disabled={!form.name.trim() || (!form.id && !form.value) || saveMutation.isPending}
                className="px-4 py-2 bg-blue-600 text-white text-sm rounded-md hover:bg-blue-700 disabled:opacity-50"
              >
                {saveMutation.isPending ? t('common:saving') : (form.id ? t('common:update') : t('common:create'))}
              </button>
            </div>
        </ModalShell>
      )}
    </div>
  );
}
