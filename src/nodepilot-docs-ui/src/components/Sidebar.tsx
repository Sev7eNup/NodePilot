import { useState } from 'react'
import { NavLink } from 'react-router-dom'
import { navGroups } from '../data/nav'
import { ChevronRightIcon } from '../lib/icons'

interface SidebarProps {
  /** Active content path, e.g. "getting-started/introduction". */
  current: string
  onNavigate?: () => void
}

/**
 * Sidebar groups are expanded by default. Once a user manually expands or
 * collapses a group, that choice is remembered across page navigations via a
 * module-level map (in-memory only — resets on a full page reload).
 */
const groupState = new Map<string, boolean>()

export default function Sidebar({ current, onNavigate }: SidebarProps) {
  return (
    <nav className="np-scroll flex flex-col gap-1 overflow-y-auto pr-2 text-sm">
      {navGroups.map((group) => (
        <SidebarGroup
          key={group.label}
          label={group.label}
          items={group.items}
          current={current}
          onNavigate={onNavigate}
        />
      ))}
    </nav>
  )
}

function SidebarGroup({
  label,
  items,
  current,
  onNavigate,
}: {
  label: string
  items: { path: string; title: string }[]
  current: string
  onNavigate?: () => void
}) {
  const [open, setOpen] = useState<boolean>(() => {
    const stored = groupState.get(label)
    if (stored !== undefined) return stored
    // Auto-expand the group containing the current page.
    return items.some((i) => i.path === current)
  })

  const toggle = () => {
    const next = !open
    setOpen(next)
    groupState.set(label, next)
  }

  return (
    <div className="mb-1">
      <button
        type="button"
        onClick={toggle}
        className="flex w-full items-center gap-1 px-2 py-1.5 text-[0.72rem] font-semibold uppercase tracking-wider text-[var(--color-on-surface-variant)] hover:text-[var(--color-on-surface)]"
      >
        <ChevronRightIcon
          className={`h-3.5 w-3.5 shrink-0 transition-transform ${open ? 'rotate-90' : ''}`}
        />
        <span>{label}</span>
      </button>
      {open && (
        <ul className="ml-1.5 border-l border-[var(--color-outline-variant)]">
          {items.map((item) => {
            const active = current === item.path
            return (
              <li key={item.path}>
                <NavLink
                  to={`/${item.path}`}
                  onClick={onNavigate}
                  className={`-ml-px block border-l-2 px-3 py-1.5 transition-colors ${
                    active
                      ? 'border-[var(--np-accent)] bg-[var(--np-accent-soft)] font-medium text-[var(--np-accent-text)]'
                      : 'border-transparent text-[var(--color-on-surface-variant)] hover:border-[var(--color-outline-variant)] hover:text-[var(--color-on-surface)]'
                  }`}
                >
                  {item.title}
                </NavLink>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}