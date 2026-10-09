import { useTranslation } from 'react-i18next';

export function WorkflowPageControls({ page, totalPages, busy, onChange }: Readonly<{
  page: number; totalPages: number; busy: boolean; onChange: (page: number) => void;
}>) {
  const { t } = useTranslation('workflows');
  if (totalPages < 2 && page === 1) return null;
  return <div className="flex shrink-0 items-center justify-between gap-2 border-t border-outline-variant/20 px-2 py-1 text-xs">
    <button type="button" disabled={busy || page <= 1} onClick={() => onChange(page - 1)}
      aria-label={t('pagination.previous')} className="disabled:opacity-40">‹</button>
    <span>{t('pagination.pageOf', { page, totalPages: Math.max(1, totalPages) })}</span>
    <button type="button" disabled={busy || page >= totalPages} onClick={() => onChange(page + 1)}
      aria-label={t('pagination.next')} className="disabled:opacity-40">›</button>
  </div>;
}
