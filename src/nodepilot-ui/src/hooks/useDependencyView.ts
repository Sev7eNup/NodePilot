import { useLayoutEffect, useRef } from 'react';
import { useLocation } from 'react-router';
import { createStore } from 'zustand/vanilla';
import type { Viewport } from '@xyflow/react';
import { captureAuthBoundaryGeneration, isAuthBoundaryGenerationCurrent, registerAuthBoundaryLiveStateClearer } from '../security/authBoundary';

interface ViewMemory {
  scrollTop?: number;
  detailsScrollTop?: number;
  expandedSections?: string[];
  viewport?: Viewport;
  count?: number;
  expanded?: Record<string, boolean>;
}

// UI positions only; API objects remain in the permission-scoped query cache.
export const dependencyViewMemory = createStore<{
  entries: Map<string, ViewMemory>;
  remember: (key: string, patch: Partial<ViewMemory>) => void;
}>(set => ({
  entries: new Map(),
  remember: (key, patch) => set(state => {
    const entries = new Map(state.entries);
    entries.set(key, { ...entries.get(key), ...patch });
    if (entries.size > 100) entries.delete(entries.keys().next().value!);
    return { entries };
  }),
}));
registerAuthBoundaryLiveStateClearer(() => dependencyViewMemory.setState({ entries: new Map() }));

export function useDependencyView(ready: boolean) {
  const { key } = useLocation();
  const page = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const scroller = document.getElementById('np-main-scroll');
    const element = page.current;
    if (!ready || !scroller || !element) return;
    const generation = captureAuthBoundaryGeneration();
    const saved = dependencyViewMemory.getState().entries.get(key);
    const sections = () => Array.from(element.querySelectorAll<HTMLDetailsElement>('details[data-dependency-section]'));
    // The details panel scrolls on its own in the wide layout.
    const details = () => element.querySelector<HTMLElement>('.np-dependency-details');
    function save() {
      if (!isAuthBoundaryGenerationCurrent(generation)) return;
      dependencyViewMemory.getState().remember(key, {
        scrollTop: scroller!.scrollTop,
        detailsScrollTop: details()?.scrollTop ?? 0,
        expandedSections: sections().filter(s => s.open).map(s => s.dataset.dependencySection!),
      });
    }
    function restore() {
      if (!saved || !isAuthBoundaryGenerationCurrent(generation)) return;
      sections().forEach(s => { s.open = saved.expandedSections?.includes(s.dataset.dependencySection!) ?? false; });
      scroller!.scrollTop = saved.scrollTop ?? 0;
      const panel = details(); if (panel) panel.scrollTop = saved.detailsScrollTop ?? 0;
    }
    restore();
    const frame = requestAnimationFrame(restore);
    scroller.addEventListener('scroll', save, { passive: true });
    element.addEventListener('scroll', save, { capture: true, passive: true });
    return () => {
      cancelAnimationFrame(frame);
      scroller.removeEventListener('scroll', save);
      element.removeEventListener('scroll', save, { capture: true });
      save();
    };
  }, [key, ready]);
  return page;
}
