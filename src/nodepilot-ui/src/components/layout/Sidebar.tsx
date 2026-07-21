import { useEffect, useMemo, useRef, useState } from 'react';
import { NavLink } from 'react-router-dom';
import type { CarbonIconType } from '@carbon/icons-react';
import {
  Screen, Settings, Logout, Light, Contrast, Asleep, ColorPalette,
  ChevronLeft, ChevronRight, Close, Building, BankVault, Checkmark,
  Star, Search, OverflowMenuHorizontal,
} from '@carbon/icons-react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { getKnowledgeCapabilities } from '../../api/ai';
import { useAuthStore } from '../../stores/authStore';
import { useThemeStore, THEMES, type Theme } from '../../stores/themeStore';
import { useLangStore } from '../../stores/langStore';
import { useSidebarStore } from '../../stores/sidebarStore';
import { useIsMobile } from '../../hooks/useMediaQuery';
import { useSidebarBadges, type SidebarBadges } from '../../hooks/useSidebarBadges';
import { BrandLogo } from '../BrandLogo';
import type { AppLang } from '../../i18n';
import { navGroups, type BadgeKind } from '../../lib/navigation';

// Icon per skin id (+ system). Falls back to Monitor for any future skin without
// an explicit icon. Options/cycle are derived from the THEMES registry so a new
// scheme appears in the quick-toggle + popover automatically.
const THEME_ICONS: Record<string, CarbonIconType> = { light: Light, dark: Asleep, 'dark-lila': ColorPalette, 'light-grey': Contrast, 'dark-sparkasse': BankVault, 'light-sparkasse': Building, 'dark-nebula': Star, system: Screen };

const THEME_OPTIONS: { value: Theme; icon: CarbonIconType; key: string }[] = [
  ...THEMES.map((t) => ({ value: t.id as Theme, icon: THEME_ICONS[t.id] ?? Screen, key: t.labelKey })),
  { value: 'system' as Theme, icon: Screen, key: 'themeSystem' },
];

const THEME_CYCLE: Theme[] = THEME_OPTIONS.map((o) => o.value);

const LANG_OPTIONS: { value: AppLang; label: string }[] = [
  { value: 'de', label: 'DE' },
  { value: 'en', label: 'EN' },
];

/** Trailing count badge for a nav item. Neutral for totals, accent for running activity,
 *  warning for alerts, and a static pulsing pill for the live-ops entry. Renders nothing
 *  when the count is unavailable (loading / role-gated) or a zero activity count. */
function NavBadge({ kind, badges, liveLabel }: { kind: BadgeKind; badges: SidebarBadges; liveLabel: string }) {
  if (kind === 'live') {
    return (
      <span className="np-nav-badge is-live">
        <span className="np-live-dot" />
        {liveLabel}
      </span>
    );
  }
  const value = kind === 'workflows' ? badges.workflows
    : kind === 'running' ? badges.running
      : kind === 'machines' ? badges.machines
        : badges.alerts;
  if (value == null) return null;
  // Activity counters (running / alerts) hide at zero — a "0" there is noise, not signal.
  if ((kind === 'running' || kind === 'alerts') && value <= 0) return null;
  const cls = kind === 'running' ? ' is-running' : kind === 'alerts' ? ' is-alerts' : '';
  return <span className={`np-nav-badge${cls}`}>{value}</span>;
}

/** Gradient avatar tile. Size/shape come from the caller so the same treatment serves the
 *  42px footer panel and the small collapsed-rail avatar. */
function UserAvatar({ username, className }: { username: string; className: string }) {
  return (
    <div className="relative shrink-0 select-none">
      <div className={`np-avatar grid place-items-center font-extrabold ${className}`}>
        {username[0].toUpperCase()}
      </div>
    </div>
  );
}

export function Sidebar({ mobileOpen = false, onClose }: Readonly<{ mobileOpen?: boolean; onClose?: () => void }> = {}) {
  const { t } = useTranslation(['nav']);
  const { logout, username, role } = useAuthStore();
  const { theme, setTheme } = useThemeStore();
  const { lang, setLang } = useLangStore();
  const { collapsed, setCollapsed } = useSidebarStore();
  const isMobile = useIsMobile();
  const badges = useSidebarBadges();
  // Gates the AI-Chat nav entry: hidden until both master switches (Llm + AiKnowledge) are on.
  const { data: aiCaps } = useQuery({
    queryKey: ['ai-knowledge-capabilities'],
    queryFn: getKnowledgeCapabilities,
    staleTime: 60_000,
  });

  // Inside the drawer the icon-only rail makes no sense — always show the full layout.
  // The persisted `collapsed` preference only applies to the static desktop sidebar.
  const effectiveCollapsed = collapsed && !isMobile;

  const visibleGroups = useMemo(
    () => navGroups
      .map((g) => ({
        ...g,
        items: g.items.filter((n) =>
          (!n.adminOnly || role === 'Admin')
          && (!n.capabilityKey || Boolean(aiCaps?.[n.capabilityKey]))),
      }))
      .filter((g) => g.items.length > 0),
    [role, aiCaps],
  );

  // Live nav filter: matches the translated label. Only active in the full (non-rail) layout.
  const [filter, setFilter] = useState('');
  const query = filter.trim().toLowerCase();
  const filteredGroups = useMemo(() => {
    if (effectiveCollapsed || !query) return visibleGroups;
    return visibleGroups
      .map((g) => ({ ...g, items: g.items.filter((n) => t(`nav:${n.key}`).toLowerCase().includes(query)) }))
      .filter((g) => g.items.length > 0);
  }, [visibleGroups, query, effectiveCollapsed, t]);

  const [themeOpen, setThemeOpen] = useState(false);
  const themeRef = useRef<HTMLDivElement>(null);
  const [accountOpen, setAccountOpen] = useState(false);
  const accountRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  // Set when Ctrl-K expands a collapsed rail, so the effect below focuses the search once
  // the input has actually rendered.
  const pendingSearchFocus = useRef(false);

  useEffect(() => {
    if (!themeOpen && !accountOpen) return;
    const handler = (e: MouseEvent) => {
      if (themeOpen && themeRef.current && !themeRef.current.contains(e.target as Node)) setThemeOpen(false);
      if (accountOpen && accountRef.current && !accountRef.current.contains(e.target as Node)) setAccountOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [themeOpen, accountOpen]);

  // Global Ctrl/Cmd-K → focus the sidebar search, expanding the rail first if collapsed.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
        e.preventDefault();
        if (collapsed && !isMobile) {
          pendingSearchFocus.current = true;
          setCollapsed(false);
        } else {
          searchRef.current?.focus();
        }
      }
    };
    globalThis.addEventListener('keydown', onKey);
    return () => globalThis.removeEventListener('keydown', onKey);
  }, [collapsed, isMobile, setCollapsed]);

  // Focus the search after a Ctrl-K expanded the rail (input only exists once expanded).
  useEffect(() => {
    if (!effectiveCollapsed && pendingSearchFocus.current) {
      pendingSearchFocus.current = false;
      searchRef.current?.focus();
    }
  }, [effectiveCollapsed]);

  const cycleTheme = () => {
    const idx = THEME_CYCLE.indexOf(theme);
    setTheme(THEME_CYCLE[(idx + 1) % THEME_CYCLE.length]);
  };

  const closeDrawerAndClearFilter = () => { setFilter(''); onClose?.(); };

  const ThemeIcon = THEME_ICONS[theme] ?? Screen;
  const activeThemeKey = THEME_OPTIONS.find((o) => o.value === theme)?.key ?? 'themeLight';
  const roleTitle = role ? t(`nav:roleTitle.${role}`, { defaultValue: role }) : '';

  return (
    <aside
      className={`np-sidebar flex flex-col h-full transition-all duration-200 ease-in-out ${
        isMobile
          ? `fixed inset-y-0 left-0 z-40 w-72 ${mobileOpen ? 'translate-x-0' : '-translate-x-full'}`
          : `${effectiveCollapsed ? 'w-14' : 'np-sidebar-expanded'} shrink-0`
      }`}
    >
      {/* Header: brand mark + wordmark + ENTERPRISE / collapse (desktop) or close (drawer) */}
      {effectiveCollapsed ? (
        <div className="flex flex-col items-center gap-2 px-2 pt-4 pb-3">
          <div className="relative">
            <div className="np-brand-mark w-10 h-10 rounded-xl grid place-items-center">
              <BrandLogo alt="NodePilot logo" className="w-5 h-5" />
            </div>
          </div>
          <button
            onClick={() => setCollapsed(false)}
            title={t('nav:expandSidebar')}
            aria-label={t('nav:expandSidebar')}
            className="np-collapse-btn p-1.5 rounded-lg text-on-surface-variant hover:text-on-surface border border-transparent transition-all duration-200"
          >
            <ChevronRight size={15} />
          </button>
        </div>
      ) : (
        <div className="flex items-start gap-2 px-[18px] pt-[22px] pb-0">
          <div className="flex items-center gap-3 flex-1 min-w-0">
            <div className="relative shrink-0">
              <div className="np-brand-mark w-[42px] h-[42px] rounded-[13px] grid place-items-center">
                <BrandLogo alt="NodePilot logo" className="w-6 h-6" />
              </div>
            </div>
            <div className="min-w-0">
              <div className="flex items-center gap-2">
                <h1 className="font-headline text-[16px] font-bold leading-none truncate bg-gradient-to-r from-primary to-primary-container bg-clip-text text-transparent">NodePilot</h1>
                <span className="np-brand-edition shrink-0">{t('nav:enterprise')}</span>
              </div>
              <p className="mt-1 text-[10px] font-semibold uppercase tracking-[0.09em] text-on-surface-variant/70 leading-none truncate">{t('nav:appTagline')}</p>
            </div>
          </div>
          {isMobile ? (
            <button
              onClick={onClose}
              title={t('nav:closeMenu')}
              aria-label={t('nav:closeMenu')}
              className="shrink-0 -mr-1 p-1 rounded text-on-surface-variant hover:bg-surface-highest hover:text-on-surface transition-colors"
            >
              <Close size={18} />
            </button>
          ) : (
            <button
              onClick={() => setCollapsed(true)}
              title={t('nav:collapseSidebar')}
              aria-label={t('nav:collapseSidebar')}
              className="np-collapse-btn shrink-0 -mr-1 p-1.5 rounded-lg text-on-surface-variant hover:text-on-surface border border-transparent transition-all duration-200"
            >
              <ChevronLeft size={15} />
            </button>
          )}
        </div>
      )}

      {/* Search — live-filters the nav by label; Ctrl/Cmd-K focuses it. Hidden in the rail.
          Spacing mirrors the mock: 20px below the brand, 16px above the nav. */}
      {!effectiveCollapsed && (
        <div className="px-[18px] pt-5 pb-4">
          <div className="relative">
            <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 text-on-surface-variant/60 pointer-events-none" />
            <input
              ref={searchRef}
              type="search"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Escape') { setFilter(''); e.currentTarget.blur(); } }}
              placeholder={t('nav:searchPlaceholder')}
              aria-label={t('nav:searchPlaceholder')}
              autoComplete="off"
              spellCheck={false}
              className="np-sb-search-input"
            />
            <span className="np-sb-search-kbd absolute right-2 top-1/2 -translate-y-1/2 pointer-events-none select-none">
              Ctrl K
            </span>
          </div>
        </div>
      )}

      {/* Nav groups — scrollable so a short viewport (phone in the off-canvas drawer)
          never pushes the bottom action area off-screen; the bottom area stays pinned. */}
      <nav className={`flex-1 min-h-0 overflow-y-auto overflow-x-hidden pb-[22px] ${effectiveCollapsed ? 'px-2 pt-2' : 'px-3 pt-1'}`}>
        {filteredGroups.map((group, gi) => (
          <div key={group.labelKey} className={effectiveCollapsed ? (gi > 0 ? 'mt-2' : '') : (gi > 0 ? 'mt-[18px]' : 'mt-1.5')}>
            {!effectiveCollapsed && (
              <div className="np-sb-section-title mb-[5px]">{t(`nav:${group.labelKey}`)}</div>
            )}
            {effectiveCollapsed && gi > 0 && <div className="mx-2 mb-2 border-t border-outline/30" />}
            <div className="grid gap-[3px]">
              {group.items.map(({ to, icon: Icon, key, badge }) => {
                const label = t(`nav:${key}`);
                return (
                  <div key={to} className="relative group/navitem">
                    <NavLink
                      to={to}
                      end={to === '/'}
                      onClick={closeDrawerAndClearFilter}
                      className={`np-nav${effectiveCollapsed ? ' np-nav-rail' : ''}`}
                    >
                      {() => (
                        <>
                          <span className="np-nav-icon">
                            <Icon size={18} aria-hidden />
                          </span>
                          {!effectiveCollapsed && <span className="truncate">{label}</span>}
                          {!effectiveCollapsed && badge && <NavBadge kind={badge} badges={badges} liveLabel={t('nav:live')} />}
                        </>
                      )}
                    </NavLink>
                    {effectiveCollapsed && (
                      <span className="absolute left-full top-1/2 -translate-y-1/2 ml-2.5 px-2.5 py-1.5 bg-surface-highest text-on-surface text-xs rounded-md shadow-md pointer-events-none opacity-0 group-hover/navitem:opacity-100 transition-opacity whitespace-nowrap z-50">
                        {label}
                      </span>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        ))}
        {!effectiveCollapsed && query && filteredGroups.length === 0 && (
          <p className="px-3 py-6 text-xs text-on-surface-variant/50 text-center">{t('nav:searchNoResults')}</p>
        )}
      </nav>

      {/* Bottom area — extra bottom padding clears the phone's system navigation bar
          (env(safe-area-inset-bottom); 0 on desktop) so the controls aren't hidden. */}
      <div className={`border-t border-outline-variant/60 pt-3 pb-[calc(0.75rem_+_env(safe-area-inset-bottom))] ${effectiveCollapsed ? 'px-2' : 'px-3'}`}>
        {effectiveCollapsed ? (
          <div className="space-y-1">
            {username && (
              <div className="relative group/user flex justify-center py-1">
                <UserAvatar username={username} className="w-8 h-8 rounded-full text-sm" />
                <span className="absolute left-full top-1/2 -translate-y-1/2 ml-2.5 px-2.5 py-1.5 bg-surface-highest text-on-surface text-xs rounded-md shadow-md pointer-events-none opacity-0 group-hover/user:opacity-100 transition-opacity whitespace-nowrap z-50">
                  {username}
                  {role && <span className="ml-1.5 text-primary font-medium">({role})</span>}
                </span>
              </div>
            )}
            <div className="border-t border-outline-variant/40 my-0.5" />
            <button
              title={t(`nav:${activeThemeKey}`)}
              aria-label={t(`nav:${activeThemeKey}`)}
              onClick={cycleTheme}
              className="w-full flex justify-center py-1.5 rounded text-on-surface-variant hover:bg-surface-highest hover:text-on-surface transition-colors"
            >
              <ThemeIcon size={16} />
            </button>
            <button
              title={t(lang === 'de' ? 'nav:languageDe' : 'nav:languageEn')}
              onClick={() => setLang(lang === 'de' ? 'en' : 'de')}
              className="w-full flex justify-center py-1.5 rounded text-xs font-semibold tracking-wide text-on-surface-variant hover:bg-surface-highest hover:text-on-surface transition-colors"
            >
              {lang.toUpperCase()}
            </button>
            <div className="border-t border-outline-variant/40 my-0.5" />
            <button
              onClick={logout}
              title={t('nav:logout')}
              className="w-full flex justify-center py-1.5 rounded text-on-surface-variant hover:bg-red-500/10 hover:text-red-500 transition-colors"
            >
              <Logout size={16} />
            </button>
          </div>
        ) : (
          <>
            {username && (
              // Wrapper is position:relative but NOT overflow-hidden so the upward "…" popover
              // isn't clipped; the .np-profile-card supplies the panel styling.
              <div ref={accountRef} className="relative">
                <div className="np-profile-card">
                  <UserAvatar username={username} className="w-[42px] h-[42px] rounded-[13px] text-sm" />
                  <div className="min-w-0">
                    <div className="flex items-center gap-1.5">
                      <span className="text-[13.5px] text-on-surface font-semibold truncate">{username}</span>
                      {role && <span className="np-account-badge shrink-0">{role}</span>}
                    </div>
                    {roleTitle && <p className="mt-1 text-[10px] text-on-surface-variant/70 truncate">{roleTitle}</p>}
                  </div>
                  <button
                    aria-label={t('nav:accountMenu')}
                    onClick={() => setAccountOpen((o) => !o)}
                    className="np-account-menu-btn"
                  >
                    <OverflowMenuHorizontal size={18} />
                  </button>
                </div>
                {accountOpen && (
                  <div className="absolute bottom-full mb-1.5 right-0 w-max min-w-[160px] bg-surface-container border border-outline-variant/30 rounded-xl shadow-lg py-1 z-50">
                    <NavLink
                      to="/settings"
                      onClick={() => { setAccountOpen(false); closeDrawerAndClearFilter(); }}
                      className="flex items-center gap-2.5 px-3 py-1.5 text-sm text-on-surface-variant hover:bg-surface-highest hover:text-on-surface transition-colors"
                    >
                      <Settings size={15} />
                      <span>{t('nav:settings')}</span>
                    </NavLink>
                    <button
                      onClick={() => { setAccountOpen(false); logout(); }}
                      className="w-full flex items-center gap-2.5 px-3 py-1.5 text-sm text-on-surface-variant hover:bg-red-500/10 hover:text-red-500 transition-colors"
                    >
                      <Logout size={15} />
                      <span>{t('nav:logout')}</span>
                    </button>
                  </div>
                )}
              </div>
            )}

            <div className="flex items-center gap-2 mt-[9px]">
              {/* Theme dropdown — icon-only trigger, popover opens upward */}
              <div ref={themeRef} className="relative">
                <button
                  title={t(`nav:${activeThemeKey}`)}
                  aria-label={t(`nav:${activeThemeKey}`)}
                  onClick={() => setThemeOpen((o) => !o)}
                  className="np-skin-btn"
                >
                  <ThemeIcon size={16} />
                </button>
                {themeOpen && (
                  <div className="absolute bottom-full mb-1.5 left-0 w-max min-w-[176px] bg-surface-container border border-outline-variant/30 rounded-xl shadow-lg py-1 z-50">
                    {THEME_OPTIONS.map(({ value, icon: TIcon, key }) => (
                      <button
                        key={value}
                        onClick={() => { setTheme(value); setThemeOpen(false); }}
                        className="w-full flex items-center gap-2.5 px-3 py-1.5 text-sm text-on-surface-variant hover:bg-surface-highest hover:text-on-surface transition-colors"
                      >
                        <TIcon size={15} className={theme === value ? 'text-primary' : ''} />
                        <span className={`flex-1 text-left ${theme === value ? 'text-on-surface font-medium' : ''}`}>{t(`nav:${key}`)}</span>
                        {theme === value && <Checkmark size={13} className="text-primary shrink-0" />}
                      </button>
                    ))}
                  </div>
                )}
              </div>
              <div className="np-lang-switch">
                {LANG_OPTIONS.map(({ value, label }) => (
                  <button
                    key={value}
                    title={t(value === 'de' ? 'nav:languageDe' : 'nav:languageEn')}
                    onClick={() => setLang(value)}
                    className={`np-lang-btn${lang === value ? ' is-active' : ''}`}
                  >
                    {label}
                  </button>
                ))}
              </div>
            </div>

            <button onClick={logout} className="np-logout-btn mt-[7px]">
              <Logout size={15} />
              {t('nav:logout')}
            </button>
          </>
        )}
      </div>
    </aside>
  );
}
