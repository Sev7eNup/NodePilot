import { useState } from 'react';

/**
 * Centered workflow-name field for the editor header's middle zone. An always-present
 * `<input>` keeps rename wiring (and tests that query it via display value / textbox role)
 * intact; an in-flow styled layer renders the name with a muted/monospace leading
 * version-like token (e.g. "2.07.05.01") + the rest bold AND sizes the field so the input
 * grows to the FULL name — the name is never truncated as long as it fits the middle zone
 * (only an extreme, wider-than-the-zone name ellipsises). While the input is focused the
 * styled layer hides and the input text becomes visible for editing.
 */
export function WorkflowNameField({ name, onRename, canWrite, placeholder }: Readonly<{
  name: string;
  onRename: (value: string) => void;
  canWrite: boolean;
  placeholder: string;
}>) {
  const [focused, setFocused] = useState(false);

  // Split off a leading version-like token ("2.07.05.01 Foo" → "2.07.05.01" + "Foo").
  const match = /^(\S+)\s+(.+)$/.exec(name);
  const versionPrefix = match && /^[\d][\d.]*$/.test(match[1]) ? match[1] : null;
  const rest = versionPrefix ? match![2] : name;

  return (
    // inline-grid with both layers in the same cell: the styled layer is IN-FLOW so it sizes
    // the cell to the full name (up to max-w-full); the input fills that cell (w-full).
    <div className="inline-grid max-w-full items-center">
      <div
        aria-hidden
        className={`[grid-area:1/1] flex min-w-0 items-baseline justify-center gap-2 ${focused ? 'invisible' : ''}`}
      >
        {versionPrefix && <span className="shrink-0 font-mono text-xs text-on-surface-variant">{versionPrefix}</span>}
        <span className="truncate font-label text-sm font-semibold text-on-surface">
          {rest || <span className="text-on-surface-variant/60">{placeholder}</span>}
        </span>
      </div>
      <input
        type="text"
        value={name}
        onChange={(e) => onRename(e.target.value)}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        disabled={!canWrite}
        placeholder={placeholder}
        aria-label={placeholder}
        title={name}
        className={`[grid-area:1/1] w-full min-w-0 bg-transparent p-0 text-center text-sm font-label font-semibold outline-none disabled:cursor-default disabled:opacity-100 ${
          focused ? 'text-on-surface' : 'text-transparent placeholder:text-transparent'
        }`}
      />
    </div>
  );
}
