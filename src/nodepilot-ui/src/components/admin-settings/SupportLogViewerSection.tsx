import { DataTable, Document, Download, Help, Pause, Play } from '@carbon/icons-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { diagnostics } from '../../api/diagnostics';
import { SupportEventsTable } from './SupportEventsTable';

type LevelFilter = 'all' | 'info' | 'warn' | 'error';
type ViewMode = 'table' | 'plain';

// Höhe des Plain-Text-Log-Fensters — per Drag-Handle am unteren Rand verstellbar, in
// localStorage persistiert. Spiegelt bewusst die resizable Höhe der DB-Tabelle
// (SupportEventsTable) 1:1 wider, damit sich beide Views identisch anfühlen.
const PLAIN_DEFAULT_HEIGHT = 640;
const PLAIN_MIN_HEIGHT = 240;
const PLAIN_HEIGHT_STORAGE_KEY = 'nodepilot.supportLog.plainHeight.v1';

/**
 * Support-Log-Viewer (standalone Page /support-log, Admin-only). Zwei View-Modes:
 *  - <b>Tabelle (DB)</b>: strukturierte SupportEvents aus der DB-Projektion (Default).
 *    Liefert Filter, Sortierung, Cursor-Pagination, Export — die Source-of-Truth für
 *    den Enterprise-Viewer.
 *  - <b>Plain-Text (Datei)</b>: Live-Tail der <c>nodepilot-support-*.log</c>-Datei mit
 *    Download-Button. Fallback wenn die DB-Projektion disabled ist oder der Operator
 *    den rohen RDP-Style-Tail braucht.
 */
export function SupportLogViewerSection() {
  const [mode, setMode] = useState<ViewMode>('table');
  return (
    <div className="np-card p-4 space-y-3">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <h3 className="font-semibold text-on-surface flex items-center gap-2">
          <Help size={18} /> Support-Log
        </h3>
        <div className="inline-flex rounded border border-outline-variant overflow-hidden text-xs">
          <button type="button" onClick={() => setMode('table')}
            className={`flex items-center gap-1 px-3 py-1.5 ${
              mode === 'table' ? 'bg-blue-600 text-white' : 'hover:bg-surface-low'
            }`}>
            <DataTable size={12} /> Tabelle (DB)
          </button>
          <button type="button" onClick={() => setMode('plain')}
            className={`flex items-center gap-1 px-3 py-1.5 border-l border-outline-variant ${
              mode === 'plain' ? 'bg-blue-600 text-white' : 'hover:bg-surface-low'
            }`}>
            <Document size={12} /> Plain-Text (Datei)
          </button>
        </div>
      </div>
      {mode === 'table' ? <SupportEventsTable /> : <PlainTextTailView />}
    </div>
  );
}

function PlainTextTailView() {
  const { t } = useTranslation('adminSettings');
  const [lines, setLines] = useState(200);
  const [filter, setFilter] = useState<LevelFilter>('all');
  const [autoScroll, setAutoScroll] = useState(true);
  const [paused, setPaused] = useState(false);
  const [downloadDate, setDownloadDate] = useState(() => new Date().toISOString().slice(0, 10));
  const scrollRef = useRef<HTMLPreElement | null>(null);

  // Resizable Fenster-Höhe (Drag-Handle unten + localStorage), 1:1 wie die DB-Tabelle.
  const [viewHeight, setViewHeight] = useState<number>(() => {
    if (typeof window === 'undefined') return PLAIN_DEFAULT_HEIGHT;
    try {
      const raw = globalThis.localStorage.getItem(PLAIN_HEIGHT_STORAGE_KEY);
      const n = raw ? Number.parseInt(raw, 10) : Number.NaN;
      return Number.isFinite(n) && n >= PLAIN_MIN_HEIGHT ? n : PLAIN_DEFAULT_HEIGHT;
    } catch { return PLAIN_DEFAULT_HEIGHT; }
  });
  useEffect(() => {
    if (typeof window === 'undefined') return;
    try { globalThis.localStorage.setItem(PLAIN_HEIGHT_STORAGE_KEY, String(viewHeight)); } catch { /* quota */ }
  }, [viewHeight]);
  const startHeightResize = useCallback((startY: number, startHeight: number) => {
    const move = (ev: PointerEvent) => setViewHeight(Math.max(PLAIN_MIN_HEIGHT, startHeight + (ev.clientY - startY)));
    const up = () => {
      globalThis.removeEventListener('pointermove', move);
      globalThis.removeEventListener('pointerup', up);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    globalThis.addEventListener('pointermove', move);
    globalThis.addEventListener('pointerup', up);
    document.body.style.cursor = 'row-resize';
    document.body.style.userSelect = 'none';
  }, []);

  const { data, isLoading, error } = useQuery({
    queryKey: ['diagnostics', 'support-log', lines],
    queryFn: () => diagnostics.tailSupportLog(lines),
    refetchInterval: paused ? false : 5000,
    refetchOnWindowFocus: true,
  });

  const visibleLines = useMemo(() => {
    if (!data?.lines) return [];
    if (filter === 'all') return data.lines;
    return data.lines.filter((l) => {
      if (filter === 'info') return /\[(INFO|WARN|ERR\s|FATL)\]/.test(l);
      if (filter === 'warn') return /\[(WARN|ERR\s|FATL)\]/.test(l);
      if (filter === 'error') return /\[(ERR\s|FATL)\]/.test(l);
      return true;
    });
  }, [data, filter]);

  useEffect(() => {
    if (!autoScroll || paused) return;
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [visibleLines, autoScroll, paused]);

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-end gap-2 text-sm">
        <select value={filter} onChange={(e) => setFilter(e.target.value as LevelFilter)}
          className="px-2 py-1 border border-outline-variant rounded text-xs">
          <option value="all">alle Level</option>
          <option value="info">INFO+</option>
          <option value="warn">WARN+</option>
          <option value="error">ERROR</option>
        </select>
        <select value={lines} onChange={(e) => setLines(Number.parseInt(e.target.value, 10))}
          className="px-2 py-1 border border-outline-variant rounded text-xs">
          <option value="50">50 Zeilen</option>
          <option value="100">100 Zeilen</option>
          <option value="200">200 Zeilen</option>
          <option value="500">500 Zeilen</option>
          <option value="1000">1000 Zeilen</option>
        </select>
        <label className="flex items-center gap-1 text-xs cursor-pointer">
          <input type="checkbox" checked={autoScroll} onChange={(e) => setAutoScroll(e.target.checked)} />
          Auto-Scroll
        </label>
        <button type="button" onClick={() => setPaused(!paused)}
          title={paused ? 'Polling fortsetzen' : 'Polling pausieren (zum Kopieren)'}
          className="flex items-center gap-1 px-2 py-1 text-xs border border-outline-variant rounded hover:bg-surface-low">
          {paused ? <Play size={12} /> : <Pause size={12} />} {paused ? t('supportEvents.resume') : t('supportEvents.pause')}
        </button>
      </div>

      <div className="text-xs text-on-surface-variant flex items-center gap-3">
        {data?.file ? (
          <span>Datei: <code>{data.file}</code></span>
        ) : (
          <span className="text-amber-700">Keine Datei für heute — entweder Support-Log disabled oder seit Mitternacht keine Events.</span>
        )}
        {isLoading && <span>lädt …</span>}
        {error && <span className="text-red-700">{(error as Error).message}</span>}
        <span className="ml-auto">{visibleLines.length} / {data?.lineCount ?? 0} Zeilen</span>
      </div>

      {/* Container spiegelt die Chrome der DB-Tabelle (border/rounded/surface) — der
          Plain-Text-View fügt sich damit optisch in den Rest ein statt als schwarzes
          Terminal herauszustechen. Höhe per Drag-Handle unten verstellbar. */}
      <div className="border border-outline-variant rounded overflow-hidden bg-surface-lowest">
        <pre ref={scrollRef}
          style={{ height: viewHeight }}
          className="bg-surface-lowest text-on-surface font-mono text-[11px] leading-tight p-3 overflow-auto whitespace-pre-wrap break-all">
          {visibleLines.length === 0
            ? <span className="text-on-surface-variant">— keine Zeilen sichtbar —</span>
            : visibleLines.map((l, i) => (
                <div key={i} className={lineColor(l)}>{l || ' '}</div>
              ))}
        </pre>
        {/* Bottom-Resize-Handle — identisch zur DB-Tabelle (SupportEventsTable), damit sich
            beide Views gleich anfühlen. Drag nach unten vergrößert das Fenster. */}
        <div
          onPointerDown={(e) => { e.preventDefault(); startHeightResize(e.clientY, viewHeight); }}
          className="h-1.5 bg-surface-low hover:bg-blue-500/40 cursor-row-resize border-t border-outline-variant flex items-center justify-center group"
          title={t('supportEvents.resizeLogHeight')}
        >
          <div className="w-12 h-0.5 bg-on-surface-variant/30 group-hover:bg-blue-600/60 rounded-full" />
        </div>
      </div>

      <div className="flex items-center gap-2 text-sm pt-2 border-t border-outline-variant">
        <label className="text-xs font-medium text-on-surface-variant">Tages-File downloaden:</label>
        <input type="date" value={downloadDate} onChange={(e) => setDownloadDate(e.target.value)}
          className="px-2 py-1 border border-outline-variant rounded text-xs" />
        <button type="button" onClick={() => diagnostics.downloadSupportLog(downloadDate)}
          className="flex items-center gap-1 px-3 py-1.5 text-xs bg-blue-600 text-white hover:bg-blue-700 rounded">
          <Download size={12} /> Download
        </button>
      </div>
    </div>
  );
}

// Pro-Zeile-Tönung analog zur DB-Tabelle: ERROR/FATAL rot, WARN amber, sonst neutral.
// Marker im Serilog-Text-Sink: [INFO] / [WARN] / [ERR ] / [FATL] (ERR mit Trailing-Space).
function lineColor(line: string): string {
  if (/\[(ERR\s|FATL)\]/.test(line)) return 'text-red-600 dark:text-red-400';
  if (/\[WARN\]/.test(line)) return 'text-amber-600 dark:text-amber-400';
  return 'text-on-surface';
}
