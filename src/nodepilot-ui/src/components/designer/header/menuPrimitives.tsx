import { useEffect, useId, useRef, useState, type ReactNode } from 'react';

/**
 * Shared popover mechanics for the editor-header menus (View / Tools / skin / more).
 * Returns an `open` flag, its setter, and a `ref` to attach to the popover's container
 * `<div>`. Closes on outside-click and Escape — the same pattern every header popover
 * used to reimplement inline. Generic over the container element type.
 */
export function usePopover<T extends HTMLElement = HTMLDivElement>() {
  const [open, setOpen] = useState(false);
  const ref = useRef<T>(null);
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as globalThis.Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);
  return { open, setOpen, ref };
}

/** Small uppercase section header inside a popover menu (e.g. "Darstellung" / "Overlays"). */
export function MenuSectionLabel({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <div className="px-2 pt-1.5 pb-1 text-[10px] font-label font-bold uppercase tracking-widest text-on-surface-variant">
      {children}
    </div>
  );
}

/**
 * A single action row inside a popover menu. Optional leading icon; `title` is preserved
 * (unit tests resolve moved actions via `getByTitle` after opening the menu). The row is a
 * full-width `role="menuitem"` so the whole row is the click target.
 */
export function MenuButton({ children, onClick, disabled = false, title, icon }: Readonly<{
  children: ReactNode;
  onClick: () => void;
  disabled?: boolean;
  title?: string;
  icon?: ReactNode;
}>) {
  return (
    <button
      type="button"
      role="menuitem"
      disabled={disabled}
      onClick={onClick}
      title={title}
      className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-xs font-label text-on-surface transition-colors hover:bg-surface-high disabled:cursor-not-allowed disabled:opacity-40"
    >
      {icon && <span className="shrink-0 text-on-surface-variant">{icon}</span>}
      <span className="min-w-0 flex-1 truncate">{children}</span>
    </button>
  );
}

/**
 * One toggle row: icon + label left, a small switch pill right. Rendered as a single
 * `role="switch"` button so the whole row is the click target. Used for the canvas
 * overlays (machine-coloring / failure-heatmap / …) and any other boolean toggle in a menu.
 */
export function OverlaySwitchRow({ label, title, icon, checked, onToggle, testId }: Readonly<{
  label: string;
  title: string;
  icon: ReactNode;
  checked: boolean;
  onToggle: () => void;
  testId: string;
}>) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      data-testid={testId}
      onClick={onToggle}
      title={title}
      className={`flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-xs font-label transition-colors ${
        checked ? 'bg-primary/10 text-on-surface' : 'text-on-surface-variant hover:bg-surface-high'
      }`}
    >
      <span className={`shrink-0 ${checked ? 'text-primary' : ''}`}>{icon}</span>
      <span className="min-w-0 flex-1 truncate">{label}</span>
      <span
        aria-hidden
        className={`relative h-4 w-7 shrink-0 rounded-full transition-colors ${checked ? 'bg-primary' : 'bg-outline-variant'}`}
      >
        <span
          className={`absolute top-0.5 h-3 w-3 rounded-full bg-surface-lowest shadow-sm transition-all ${checked ? 'left-3.5' : 'left-0.5'}`}
        />
      </span>
    </button>
  );
}

/** A compact visual group inside the canvas quick-settings popover. */
export function SettingsCard({ icon, title, children, className = '' }: Readonly<{
  icon: ReactNode;
  title: string;
  children: ReactNode;
  className?: string;
}>) {
  const titleId = useId();
  return (
    <section
      aria-labelledby={titleId}
      className={`rounded-xl border border-outline-variant/25 bg-surface-low/55 p-2 ${className}`}
    >
      <div className="flex items-center gap-2 px-1 pb-1.5">
        <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
          {icon}
        </span>
        <h3 id={titleId} className="text-[11px] font-label font-semibold text-on-surface-variant">
          {title}
        </h3>
      </div>
      <div className="flex flex-col gap-0.5">{children}</div>
    </section>
  );
}

/**
 * A compact labeled setting. Wide controls can sit below their label; descriptions are exposed
 * as native hover hints while the control itself carries the accessible description.
 */
export function SettingRow({ title, description, control, controlPlacement = 'right' }: Readonly<{
  title: string;
  description?: string;
  control: ReactNode;
  controlPlacement?: 'right' | 'below';
}>) {
  return (
    <div
      title={description}
      className={`rounded-lg px-2 py-1.5 transition-colors hover:bg-surface-high/55 ${
        controlPlacement === 'right'
          ? 'grid min-h-9 grid-cols-[minmax(0,1fr)_auto] items-center gap-2'
          : 'flex flex-col gap-1.5'
      }`}
    >
      <div className="min-w-0 truncate text-[11px] font-label font-medium text-on-surface">{title}</div>
      <div className={controlPlacement === 'right' ? 'shrink-0' : 'w-full'}>{control}</div>
    </div>
  );
}

/** A full-row switch so the label, not just the small pill, is an easy click target. */
export function SettingSwitchRow({ label, description, checked, onToggle, testId }: Readonly<{
  label: string;
  description: string;
  checked: boolean;
  onToggle: () => void;
  testId: string;
}>) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      aria-description={description}
      data-testid={testId}
      onClick={onToggle}
      title={description}
      className={`grid min-h-9 w-full grid-cols-[minmax(0,1fr)_auto] items-center gap-2 rounded-lg px-2 py-1.5 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60 ${
        checked ? 'bg-primary/8' : 'hover:bg-surface-high/55'
      }`}
    >
      <span className="min-w-0 truncate text-[11px] font-label font-medium text-on-surface">{label}</span>
      <span
        aria-hidden
        className={`relative h-5 w-9 shrink-0 rounded-full transition-colors ${checked ? 'bg-primary' : 'bg-outline-variant'}`}
      >
        <span
          className={`absolute top-0.5 h-4 w-4 rounded-full bg-surface-lowest shadow-sm transition-[left] ${checked ? 'left-[18px]' : 'left-0.5'}`}
        />
      </span>
    </button>
  );
}

/**
 * Single-select segmented control: an ARIA `radiogroup` of `radio` buttons. Picking a value
 * calls `onChange` with that exact value (clicking the active segment re-sets the same value —
 * a harmless no-op, never an accidental toggle). `label` names the group for assistive tech.
 */
export function SegmentedControl<T extends string>({ options, value, onChange, label, description, testId }: Readonly<{
  options: ReadonlyArray<{ value: T; label: string; title?: string }>;
  value: T;
  onChange: (value: T) => void;
  label: string;
  description?: string;
  testId?: string;
}>) {
  return (
    <div
      role="radiogroup"
      aria-label={label}
      aria-description={description}
      data-testid={testId}
      title={description}
      className="inline-flex h-8 w-full rounded-lg bg-surface-high/85 p-0.5"
    >
      {options.map((opt) => {
        const active = opt.value === value;
        return (
          <button
            key={opt.value}
            type="button"
            role="radio"
            aria-checked={active}
            title={opt.title}
            onClick={() => onChange(opt.value)}
            className={`min-w-0 flex-1 truncate rounded-md px-1.5 text-[10px] font-label font-semibold transition-colors focus-visible:z-10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/60 ${
              active ? 'bg-surface-lowest text-primary shadow-sm' : 'text-on-surface-variant hover:bg-surface-highest/60 hover:text-on-surface'
            }`}
          >
            {opt.label}
          </button>
        );
      })}
    </div>
  );
}

/**
 * `− [value] +` stepper. The `children` render the current value (a preview line, "XL", "A A +n").
 * `decLabel`/`incLabel` are the accessible names for the two buttons; the value is a live region
 * so screen readers announce changes.
 */
export function Stepper({ onDec, onInc, decDisabled = false, incDisabled = false, decLabel, incLabel, description, testId, children }: Readonly<{
  onDec: () => void;
  onInc: () => void;
  decDisabled?: boolean;
  incDisabled?: boolean;
  decLabel: string;
  incLabel: string;
  description?: string;
  testId?: string;
  children: ReactNode;
}>) {
  return (
    <div
      data-glow-unit
      data-testid={testId}
      aria-description={description}
      title={description}
      className="flex h-8 items-center overflow-hidden rounded-lg bg-surface-high/85"
    >
      <button
        type="button"
        onClick={onDec}
        disabled={decDisabled}
        aria-label={decLabel}
        className="h-full px-2 text-on-surface-variant transition-colors hover:bg-surface-highest disabled:opacity-30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-primary/60"
      >
        <span aria-hidden className="text-sm font-bold">−</span>
      </button>
      <span role="status" aria-live="polite" className="flex min-w-9 items-center justify-center px-1 text-center text-on-surface-variant">
        {children}
      </span>
      <button
        type="button"
        onClick={onInc}
        disabled={incDisabled}
        aria-label={incLabel}
        className="h-full px-2 text-on-surface-variant transition-colors hover:bg-surface-highest disabled:opacity-30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-primary/60"
      >
        <span aria-hidden className="text-sm font-bold">+</span>
      </button>
    </div>
  );
}
