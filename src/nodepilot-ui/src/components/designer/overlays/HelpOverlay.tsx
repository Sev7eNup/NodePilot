import { Close, Keyboard } from '@carbon/icons-react';
import { useMemo } from 'react';
import { Trans, useTranslation } from 'react-i18next';

/**
 * Single source of truth for the visible keyboard-shortcut catalogue. Grouped to mirror the
 * Command Palette so users see the same vocabulary in both surfaces. When a shortcut is
 * added or removed in `useEditorKeyboardShortcuts`, update this list too — the overlay is
 * the only docs surface end-users see; drift here means lost discoverability.
 *
 * Section titles + row descriptions are translated; the `keys` tokens stay literal.
 */
type TFn = (key: string) => string;

function buildSections(t: TFn): Array<{ title: string; rows: Array<{ keys: string; desc: string }> }> {
  return [
    {
      title: t('help.sections.lifecycle'),
      rows: [
        { keys: 'Ctrl + E',           desc: t('help.desc.editLockClaim') },
        { keys: 'Ctrl + U',           desc: t('help.desc.editLockRelease') },
        { keys: 'Ctrl + Shift + U',   desc: t('help.desc.forceUnlock') },
        { keys: 'Ctrl + S',           desc: t('help.desc.saveDraft') },
        { keys: 'Ctrl + Shift + S',   desc: t('help.desc.publishDisable') },
      ],
    },
    {
      title: t('help.sections.run'),
      rows: [
        { keys: 'Ctrl + Enter',         desc: t('help.desc.testRun') },
        { keys: 'Ctrl + Shift + Enter', desc: t('help.desc.debugRun') },
        { keys: 'Ctrl + Shift + X',     desc: t('help.desc.cancelRun') },
      ],
    },
    {
      title: t('help.sections.historySelection'),
      rows: [
        { keys: 'Ctrl + Z',                       desc: t('help.desc.undo') },
        { keys: 'Ctrl + Y / Ctrl + Shift + Z',    desc: t('help.desc.redo') },
        { keys: 'Ctrl + C',                       desc: t('help.desc.copy') },
        { keys: 'Ctrl + V',                       desc: t('help.desc.paste') },
        { keys: 'Ctrl + D',                       desc: t('help.desc.duplicate') },
        { keys: 'Ctrl + G',                       desc: t('help.desc.group') },
        { keys: 'Ctrl + A',                       desc: t('help.desc.selectAll') },
        { keys: 'Delete / Backspace',             desc: t('help.desc.delete') },
      ],
    },
    {
      title: t('help.sections.findNavigate'),
      rows: [
        { keys: 'Ctrl + F',         desc: t('help.desc.searchNodes') },
        { keys: 'Ctrl + H',         desc: t('help.desc.findReplace') },
        { keys: 'Ctrl + P',         desc: t('help.desc.quickSwitcher') },
        { keys: 'Ctrl + Shift + P', desc: t('help.desc.commandPalette') },
        { keys: 'Tab / Shift+Tab',  desc: t('help.desc.navConnected') },
        { keys: 'Ctrl + Shift + E', desc: t('help.desc.zoomSelection') },
      ],
    },
    {
      title: t('help.sections.layoutView'),
      rows: [
        { keys: 'Ctrl + Shift + T', desc: t('help.desc.tidy') },
        { keys: 'Ctrl + Shift + O', desc: t('help.desc.restoreLayout') },
        { keys: 'Ctrl + Shift + D', desc: t('help.desc.diff') },
        { keys: 'Ctrl + Shift + R', desc: t('help.desc.simulation') },
        { keys: 'Ctrl + Shift + L', desc: t('help.desc.lintPanel') },
        { keys: 'Ctrl + Alt + X',   desc: t('help.desc.clearFilter') },
        { keys: 'F11',              desc: t('help.desc.fullscreen') },
      ],
    },
    {
      title: t('help.sections.style'),
      rows: [
        { keys: 'A', desc: t('help.desc.edgeAnimation') },
        { keys: 'R', desc: t('help.desc.edgeRouting') },
        { keys: 'Ctrl + ] / Ctrl + [',   desc: t('help.desc.edgeWidth') },
        { keys: 'Ctrl + Shift + N',       desc: t('help.desc.nodeView') },
        { keys: 'Ctrl + Shift + > / <',   desc: t('help.desc.nodeSize') },
        { keys: 'Ctrl + Alt + . / ,',      desc: t('help.desc.labelSize') },
        { keys: 'M', desc: t('help.desc.machineColoring') },
        { keys: 'H', desc: t('help.desc.failureHeatmap') },
        { keys: 'G', desc: t('help.desc.snapToGrid') },
      ],
    },
    {
      title: t('help.sections.selectedNodeToggles'),
      rows: [
        { keys: 'D', desc: t('help.desc.toggleDisabled') },
        { keys: 'B', desc: t('help.desc.toggleBreakpoint') },
      ],
    },
    {
      title: t('help.sections.export'),
      rows: [
        { keys: 'Ctrl + Shift + J', desc: t('help.desc.exportJson') },
        { keys: 'Ctrl + Alt + P',    desc: t('help.desc.exportPng') },
      ],
    },
    {
      title: t('help.sections.navigate'),
      rows: [
        { keys: 'Ctrl + Shift + 1', desc: t('help.desc.goWorkflows') },
        { keys: 'Ctrl + Shift + 2', desc: t('help.desc.goExecutions') },
        { keys: 'Ctrl + Shift + 3', desc: t('help.desc.goMachines') },
        { keys: 'Ctrl + Shift + 4', desc: t('help.desc.goGlobals') },
        { keys: 'Ctrl + Shift + 5', desc: t('help.desc.goAudit') },
      ],
    },
    {
      title: t('help.sections.canvas'),
      rows: [
        { keys: 'Left-drag on empty canvas', desc: t('help.desc.marquee') },
        { keys: 'Middle / right-drag',       desc: t('help.desc.pan') },
        { keys: 'Shift + click',             desc: t('help.desc.extendSelection') },
      ],
    },
    {
      title: t('help.sections.general'),
      rows: [
        { keys: '?',      desc: t('help.desc.showHideHelp') },
        { keys: 'Escape', desc: t('help.desc.closeOverlays') },
      ],
    },
  ];
}

export function HelpOverlay({ onClose }: Readonly<{ onClose: () => void }>) {
  const { t } = useTranslation('editor');
  const SECTIONS = useMemo(() => buildSections(t), [t]);
  return (
    <div
      className="np-anim-backdrop fixed inset-0 z-50 flex items-center justify-center bg-black/40 backdrop-blur-[2px]"
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <div
        className="np-anim-overlay w-[1180px] max-w-[96vw] max-h-[88vh] bg-surface-lowest rounded-2xl shadow-2xl border border-outline-variant/40 overflow-hidden flex flex-col"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.stopPropagation()}
        role="presentation"
      >
        <div className="flex items-center justify-between px-5 py-3 border-b border-outline-variant/25 bg-gradient-to-b from-surface-lowest to-surface-low/30">
          <div className="flex items-center gap-2">
            <Keyboard size={18} className="text-primary/70" />
            <h2 className="font-headline text-sm font-bold text-on-surface">{t('help.title')}</h2>
          </div>
          <button onClick={onClose} className="text-on-surface-variant hover:text-on-surface" aria-label={t('common:close')}>
            <Close size={16} />
          </button>
        </div>
        <div className="overflow-y-auto p-5">
          <div className="columns-1 sm:columns-2 lg:columns-3 gap-6 [column-fill:_balance]">
            {SECTIONS.map((section) => (
              <section key={section.title} className="break-inside-avoid mb-5">
                <h3 className="text-[10px] font-label font-bold uppercase tracking-[0.1em] text-on-surface-variant/80 mb-2">
                  {section.title}
                </h3>
                <div className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5">
                  {section.rows.map((r) => (
                    <div key={r.keys} className="contents">
                      <kbd className="font-mono text-[11px] px-2 py-0.5 rounded-md bg-surface-high text-on-surface border border-outline-variant/40 self-start whitespace-nowrap">
                        {r.keys}
                      </kbd>
                      <span className="font-label text-xs text-on-surface-variant leading-snug">{r.desc}</span>
                    </div>
                  ))}
                </div>
              </section>
            ))}
          </div>
        </div>
        <div className="px-5 py-2 border-t border-outline-variant/25 bg-surface-low/40 text-[10px] font-label text-on-surface-variant/70 flex items-center justify-between">
          <span>
            <Trans
              t={t}
              i18nKey="help.footer"
              values={{ kbd: 'Ctrl+Shift+P' }}
              components={{ kbd: <kbd className="font-mono px-1 py-0.5 rounded bg-surface-high text-on-surface border border-outline-variant/40" /> }}
            />
          </span>
          <span className="font-mono">?</span>
        </div>
      </div>
    </div>
  );
}
