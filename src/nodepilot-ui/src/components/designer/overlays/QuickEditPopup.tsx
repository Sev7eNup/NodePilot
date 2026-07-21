import { Close } from '@carbon/icons-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Node } from '@xyflow/react';

/**
 * Primary editable field per activity type. `labelKey` resolves to a translated label;
 * `placeholderKey` (when present) resolves to a translated placeholder. A literal
 * `placeholder` is kept as-is for tokens that must stay verbatim (URLs, paths, query
 * syntax, JSON examples).
 */
const PRIMARY_FIELD: Record<string, { key: string; labelKey: string; multiline: boolean; placeholderKey?: string; placeholder?: string }> = {
  runScript:         { key: 'script',            labelKey: 'quickEdit.labelScript',      multiline: true,  placeholderKey: 'quickEdit.placeholderScript' },
  restApi:           { key: 'url',               labelKey: 'quickEdit.labelUrl',         multiline: false, placeholder: 'https://…' },
  serviceManagement: { key: 'serviceName',       labelKey: 'quickEdit.labelServiceName', multiline: false, placeholderKey: 'quickEdit.placeholderServiceName' },
  fileOperation:     { key: 'path',              labelKey: 'quickEdit.labelFilePath',    multiline: false, placeholder: 'C:\\…\\file.txt' },
  folderOperation:   { key: 'path',              labelKey: 'quickEdit.labelFolderPath',  multiline: false, placeholder: 'C:\\…\\Folder' },
  registryOperation: { key: 'keyPath',           labelKey: 'quickEdit.labelKeyPath',     multiline: false, placeholder: 'HKLM:\\…' },
  wmiQuery:          { key: 'className',         labelKey: 'quickEdit.labelWmiClass',    multiline: false, placeholder: 'Win32_…' },
  startProgram:      { key: 'filePath',          labelKey: 'quickEdit.labelFilePathCap', multiline: false, placeholder: 'C:\\…' },
  emailNotification: { key: 'to',               labelKey: 'quickEdit.labelTo',          multiline: false, placeholder: 'recipient@example.com' },
  sql:               { key: 'query',            labelKey: 'quickEdit.labelQuery',       multiline: true,  placeholder: 'SELECT …' },
  xmlQuery:          { key: 'xpath',            labelKey: 'quickEdit.labelXpath',       multiline: false, placeholder: '//element' },
  jsonQuery:         { key: 'jsonPath',         labelKey: 'quickEdit.labelJsonPath',    multiline: false, placeholder: '$.key' },
  log:               { key: 'message',          labelKey: 'quickEdit.labelMessage',     multiline: false, placeholderKey: 'quickEdit.placeholderMessage' },
  delay:             { key: 'seconds',          labelKey: 'quickEdit.labelSeconds',     multiline: false, placeholder: '5' },
  startWorkflow:     { key: 'workflowNameOrId', labelKey: 'quickEdit.labelWorkflow',    multiline: false, placeholderKey: 'quickEdit.placeholderWorkflow' },
  returnData:        { key: 'data',             labelKey: 'quickEdit.labelReturnData',  multiline: true, placeholder: '{"key": "{{step.output}}"}' },
};

interface Props {
  node: Node;
  screenX: number;
  screenY: number;
  onSave: (nodeId: string, configPatch: Record<string, unknown>) => void;
  onClose: () => void;
}

export function QuickEditPopup({ node, screenX, screenY, onSave, onClose }: Readonly<Props>) {
  const { t } = useTranslation('editor');
  const data = node.data as Record<string, unknown>;
  const activityType = (data.activityType as string) || 'runScript';
  const config = (data.config as Record<string, unknown>) || {};
  const field = PRIMARY_FIELD[activityType];
  const fieldLabel = field ? t(field.labelKey) : '';
  const fieldPlaceholder = field
    ? (field.placeholderKey ? t(field.placeholderKey) : field.placeholder)
    : undefined;

  const [value, setValue] = useState(() => {
    if (!field) return '';
    const v = config[field.key];
    return v != null ? String(v) : '';
  });

  const inputRef = useRef<HTMLTextAreaElement | HTMLInputElement>(null);
  const popupRef = useRef<HTMLDivElement>(null);

  // Auto-focus on open
  useEffect(() => { inputRef.current?.focus(); }, []);

  // Close on Escape
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    globalThis.addEventListener('keydown', handler);
    return () => globalThis.removeEventListener('keydown', handler);
  }, [onClose]);

  // Close on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (popupRef.current && !popupRef.current.contains(e.target as globalThis.Node)) onClose();
    };
    setTimeout(() => globalThis.addEventListener('mousedown', handler), 0);
    return () => globalThis.removeEventListener('mousedown', handler);
  }, [onClose]);

  const save = () => {
    if (!field) { onClose(); return; }
    const parsed = field.key === 'seconds' ? Number(value) : value;
    onSave(node.id, { [field.key]: parsed });
    onClose();
  };

  // Position: keep popup on screen
  const POPUP_W = 360;
  const POPUP_H = field?.multiline ? 220 : 120;
  const left = Math.min(screenX, globalThis.innerWidth - POPUP_W - 16);
  const top  = Math.max(8, screenY - POPUP_H - 12);

  if (!field) return null;

  return (
    <div
      ref={popupRef}
      className="fixed z-[9999] bg-surface-lowest border border-outline-variant/30 rounded-xl shadow-2xl p-4 flex flex-col gap-3"
      style={{ left, top, width: POPUP_W }}
      onMouseDown={(e) => e.stopPropagation()}
    >
      <div className="flex items-center justify-between">
        <span className="font-headline text-sm font-semibold text-on-surface">
          {fieldLabel}
          <span className="ml-2 font-label text-xs font-normal text-on-surface-variant">
            {(data.label as string) || activityType}
          </span>
        </span>
        <button
          onClick={onClose}
          className="text-on-surface-variant hover:text-on-surface rounded transition-colors p-0.5"
        >
          <Close size={14} />
        </button>
      </div>
      {field.multiline ? (
        <textarea
          ref={inputRef as React.RefObject<HTMLTextAreaElement>}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder={fieldPlaceholder}
          className="input-field font-mono text-xs resize-none"
          rows={6}
          onKeyDown={(e) => { if (e.key === 'Escape') onClose(); }}
        />
      ) : (
        <input
          ref={inputRef as React.RefObject<HTMLInputElement>}
          type={field.key === 'seconds' ? 'number' : 'text'}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder={fieldPlaceholder}
          className="input-field text-sm"
          onKeyDown={(e) => {
            if (e.key === 'Enter') { e.preventDefault(); save(); }
            if (e.key === 'Escape') onClose();
          }}
        />
      )}
      <div className="flex items-center justify-end gap-2">
        <button onClick={onClose} className="px-3 py-1.5 text-xs font-label font-semibold text-on-surface-variant hover:bg-surface-high rounded-md transition-colors">
          {t('common:cancel')}
        </button>
        <button
          onClick={save}
          className="px-3 py-1.5 text-xs font-label font-semibold bg-primary text-on-primary rounded-md hover:bg-primary/90 transition-colors"
        >
          {t('common:save')}{!field.multiline && <span className="ml-1.5 opacity-60 text-[10px]">↵</span>}
        </button>
      </div>
    </div>
  );
}
