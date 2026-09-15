import { create } from 'zustand';
import { persist } from 'zustand/middleware';

/** Matches the `max-w-3xl` the global AI chat used before the column became resizable, so
 *  existing profiles see no shift. Also the width a double-click on a handle restores. */
export const DEFAULT_CHAT_WIDTH = 768;
/** Below this the composer buttons and the message bubbles start to crowd each other. */
export const MIN_CHAT_WIDTH = 560;
/** Long prose stops being comfortable to read well before this; it exists for code and tables. */
export const MAX_CHAT_WIDTH = 1600;

/** The floating widget's original width, and what a double-click on its handle restores. */
export const DEFAULT_WIDGET_WIDTH = 420;
/** Narrower than this and the header actions crowd the title. */
export const MIN_WIDGET_WIDTH = 320;
/** The widget overlays the page, so it stays a panel rather than growing into a second window. */
export const MAX_WIDGET_WIDTH = 720;

interface ChatLayoutState {
  /** Preferred width of the global AI chat column in px. The rendered width is capped by the
   *  available space, so a value stored on a wide screen survives a narrow window. */
  chatWidth: number;
  setChatWidth: (width: number) => void;
  /** Preferred width of the floating chat widget in px, capped by the viewport the same way. */
  widgetWidth: number;
  setWidgetWidth: (width: number) => void;
}

export const useChatLayoutStore = create<ChatLayoutState>()(
  persist(
    (set) => ({
      chatWidth: DEFAULT_CHAT_WIDTH,
      setChatWidth: (width: number) =>
        set({ chatWidth: Math.min(MAX_CHAT_WIDTH, Math.max(MIN_CHAT_WIDTH, width)) }),
      widgetWidth: DEFAULT_WIDGET_WIDTH,
      setWidgetWidth: (width: number) =>
        set({ widgetWidth: Math.min(MAX_WIDGET_WIDTH, Math.max(MIN_WIDGET_WIDTH, width)) }),
    }),
    { name: 'nodepilot.chat-layout', version: 1 },
  ),
);
