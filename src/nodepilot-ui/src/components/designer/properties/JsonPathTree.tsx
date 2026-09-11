import { ChevronDown, ChevronRight, Copy } from '@carbon/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { buildJsonPath, describeJsonValue, type JsonPathSegment } from '../../../lib/jsonPathBuilder';

/** Children mounted per expansion step, and per click on "show more". */
const CHILDREN_PAGE_SIZE = 50;

export function JsonPathTree({
  value,
  onPick,
  maxDepth = 7,
}: Readonly<{
  value: unknown;
  onPick: (path: string) => void;
  maxDepth?: number;
}>) {
  return (
    <div className="rounded-md border border-outline-variant/30 bg-surface-low p-1.5 max-h-56 overflow-auto">
      <JsonPathTreeNode
        name="$"
        value={value}
        path={[]}
        depth={0}
        maxDepth={maxDepth}
        onPick={onPick}
      />
    </div>
  );
}

function JsonPathTreeNode({
  name,
  value,
  path,
  depth,
  maxDepth,
  onPick,
}: Readonly<{
  name: string;
  value: unknown;
  path: JsonPathSegment[];
  depth: number;
  maxDepth: number;
  onPick: (path: string) => void;
}>) {
  const { t } = useTranslation('properties');
  const expandable = value !== null && typeof value === 'object' && depth < maxDepth;
  const [open, setOpen] = useState(depth < 2);
  // How many children of this node are mounted. A step output holding a few hundred records
  // expands into a four-figure row count, and every row is a component with its own state —
  // enough to make a single click on a chevron stall. Nothing is hidden: the rest is one
  // click away and the full path of every entry stays reachable.
  const [shownChildren, setShownChildren] = useState(CHILDREN_PAGE_SIZE);
  const jsonPath = buildJsonPath(path);
  const children = getChildren(value);

  return (
    <div>
      <div
        className="group flex items-center gap-1 rounded px-1 py-0.5 hover:bg-surface-high"
        style={{ paddingLeft: depth * 12 + 4 }}
      >
        {expandable ? (
          <button
            type="button"
            onClick={() => setOpen((v) => !v)}
            className="w-4 h-4 flex items-center justify-center rounded hover:bg-surface-highest shrink-0"
            aria-label={open ? t('jsonPathTree.collapse') : t('jsonPathTree.expand')}
          >
            {open ? <ChevronDown size={11} /> : <ChevronRight size={11} />}
          </button>
        ) : (
          <span className="w-4 shrink-0" />
        )}
        <button
          type="button"
          onClick={() => onPick(jsonPath)}
          className="min-w-0 flex-1 text-left flex items-center gap-2"
          title={t('jsonPathTree.use', { path: jsonPath })}
        >
          <code className="text-[10px] font-mono text-primary shrink-0">{name}</code>
          <span className="text-[10px] text-on-surface-variant truncate">{describeJsonValue(value)}</span>
        </button>
        <button
          type="button"
          onClick={() => onPick(jsonPath)}
          className="opacity-0 group-hover:opacity-100 w-5 h-5 rounded flex items-center justify-center text-on-surface-variant hover:text-primary hover:bg-primary-fixed/50 transition-opacity"
          title={t('jsonPathTree.copy', { path: jsonPath })}
        >
          <Copy size={11} />
        </button>
      </div>
      {expandable && open && (
        <div>
          {children.slice(0, shownChildren).map(({ key, child }) => (
            <JsonPathTreeNode
              key={`${depth}-${String(key)}`}
              name={String(key)}
              value={child}
              path={[...path, key]}
              depth={depth + 1}
              maxDepth={maxDepth}
              onPick={onPick}
            />
          ))}
          {children.length > shownChildren && (
            <button
              type="button"
              onClick={() => setShownChildren((n) => n + CHILDREN_PAGE_SIZE)}
              style={{ paddingLeft: (depth + 1) * 12 + 4 }}
              className="w-full text-left py-0.5 pr-1 text-[10px] text-primary hover:underline"
            >
              {t('jsonPathTree.showMore', { count: children.length - shownChildren })}
            </button>
          )}
        </div>
      )}
    </div>
  );
}

function getChildren(value: unknown): Array<{ key: JsonPathSegment; child: unknown }> {
  if (Array.isArray(value)) return value.map((child, key) => ({ key, child }));
  if (value && typeof value === 'object') {
    return Object.entries(value as Record<string, unknown>).map(([key, child]) => ({ key, child }));
  }
  return [];
}
