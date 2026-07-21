import type { CarbonIconType } from '@carbon/icons-react';
import {
  Activity, Analytics, Api, Application, Apps, Archive, BareMetalServer, Bot, Chip,
  Cloud, Code, DataBase, DataStructured, Debug, DnsServices, Document, DocumentAdd,
  Edit, Email, Events, Flash, Folder, FolderDetails, FolderOpen, Fork, Help, Hourglass,
  Http, HybridNetworking, Locked, MagicWand, Merge, Network_3, Notebook, Notification,
  Password, PlayFilledAlt, Power, Renew, Repeat, Reply, Restart, Rocket, Screen,
  Security, Settings, Tag, TaskView, Terminal, Time, Tools, Touch_1, TreeView, Webhook,
} from '@carbon/icons-react';

/**
 * Material-Symbols glyph token -> Carbon icon component.
 *
 * The token string stays the stable identifier carried by the activity catalog
 * (`activityCatalog.generated.ts`, kept in parity with the backend `ActivityCatalog`)
 * and by persisted custom-activity definitions. This map is the single point that
 * resolves such a token to a rendered SVG, so the backend/data never has to change.
 */
export const ACTIVITY_ICON_COMPONENTS: Record<string, CarbonIconType> = {
  // --- built-in activity / trigger catalog ---
  terminal: Terminal,
  description: Document,
  folder_open: FolderOpen,
  tag: Tag,
  archive: Archive,
  settings: Settings,
  pending_actions: TaskView,
  database: DataBase,
  hard_drive: BareMetalServer,
  rocket_launch: Rocket,
  power_settings_new: Power,
  hourglass_top: Hourglass,
  language: Api,
  storage: DataBase,
  code: Code,
  data_object: DataStructured,
  mail: Email,
  edit_note: Edit,
  casino: MagicWand,
  smart_toy: Bot,
  note_add: DocumentAdd,
  schedule: Time,
  merge: Merge,
  play_circle: PlayFilledAlt,
  loop: Repeat,
  call_split: Fork,
  reply: Reply,
  touch_app: Touch_1,
  webhook: Webhook,
  folder_supervised: FolderDetails,
  event_note: Events,
  // --- non-catalog fallbacks used by the palette/categories ---
  sticky_note_2: Notebook,
  extension: Application,
  help: Help,
  // --- workflow-snippet + custom-activity picker tokens ---
  shield: Security,
  bolt: Flash,
  build: Tools,
  memory: Chip,
  dns: DnsServices,
  cloud: Cloud,
  folder: Folder,
  http: Http,
  key: Password,
  lock: Locked,
  sync: Renew,
  notifications: Notification,
  analytics: Analytics,
  monitoring: Activity,
  bug_report: Debug,
  api: Api,
  computer: Screen,
  desktop_windows: Screen,
  lan: Network_3,
  hub: HybridNetworking,
  account_tree: TreeView,
  workspaces: Apps,
  power: Power,
  restart_alt: Restart,
};

/**
 * Fallback for unknown / legacy tokens — e.g. free-text Material Symbols names persisted
 * by custom activities created before the Carbon migration. Renders a generic plugin glyph.
 *
 * Resolve at a render site with member access (NOT a wrapper function): a component obtained
 * from a call is flagged by the React Compiler as "created during render", whereas a map lookup
 * is a stable reference — same pattern the sidebar theme icons use:
 *   const Icon = ACTIVITY_ICON_COMPONENTS[token] ?? FALLBACK_ACTIVITY_ICON;
 */
export const FALLBACK_ACTIVITY_ICON: CarbonIconType = Application;

/**
 * Curated tokens offered in the custom-activity icon picker. All are Carbon-backed via the
 * map above. Kept as the previous Material-Symbols token strings so existing definitions
 * keep their icon after the migration (no data rewrite needed).
 */
export const CUSTOM_ACTIVITY_ICON_CHOICES: readonly string[] = [
  'extension', 'terminal', 'bolt', 'rocket_launch', 'settings', 'build', 'memory', 'dns',
  'cloud', 'database', 'storage', 'folder', 'description', 'mail', 'language', 'http', 'key',
  'lock', 'shield', 'tag', 'sync', 'schedule', 'notifications', 'analytics', 'monitoring',
  'bug_report', 'code', 'api', 'computer', 'desktop_windows', 'lan', 'hub', 'account_tree',
  'workspaces', 'power', 'restart_alt',
];
