import type { CarbonIconType } from '@carbon/icons-react';
import {
  Activity, Analytics, Api, Application, Apps, Archive, BareMetalServer, Bot, Chip,
  Cloud, Code, DataBase, DataStructured, Debug, DnsServices, Document, DocumentAdd,
  Earth, Edit, Email, Events, Flash, Folder, FolderDetails, FolderOpen, Fork, Help, Hourglass,
  Http, HybridNetworking, Locked, MagicWand, Merge, Network_3, Notebook, Notification,
  Password, PlayFilledAlt, Power, Renew, Repeat, Reply, Restart, Rocket, Screen,
  Security, Settings, Tag, TaskView, Terminal, Time, Tools, Touch_1, TreeView, Webhook,
} from '@carbon/icons-react';
import { CurlyBracesIcon } from './CurlyBracesIcon';

/**
 * Maps a glyph token to a Carbon icon component.
 *
 * The token is the stable identifier carried by the activity catalog
 * (`activityCatalog.generated.ts`, in parity with the backend `ActivityCatalog`) and by
 * stored custom-activity definitions. This map is the only place a token becomes an SVG,
 * so changing an icon never touches the backend or stored data.
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
  web_globe: Earth,
  storage: DataBase,
  code: Code,
  curly_braces: CurlyBracesIcon,
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
 * Generic plugin glyph used for tokens the map does not know.
 *
 * Resolve it at the render site with a map lookup, not through a wrapper function: the React
 * Compiler flags a component returned by a call as created during render, while a lookup is a
 * stable reference.
 *   const Icon = ACTIVITY_ICON_COMPONENTS[token] ?? FALLBACK_ACTIVITY_ICON;
 */
export const FALLBACK_ACTIVITY_ICON: CarbonIconType = Application;

/**
 * Tokens offered in the custom-activity icon picker. Each one resolves through the map above,
 * and the strings match the tokens already stored with existing definitions.
 */
export const CUSTOM_ACTIVITY_ICON_CHOICES: readonly string[] = [
  'extension', 'terminal', 'bolt', 'rocket_launch', 'settings', 'build', 'memory', 'dns',
  'cloud', 'database', 'storage', 'folder', 'description', 'mail', 'language', 'http', 'key',
  'lock', 'shield', 'tag', 'sync', 'schedule', 'notifications', 'analytics', 'monitoring',
  'bug_report', 'code', 'api', 'computer', 'desktop_windows', 'lan', 'hub', 'account_tree',
  'workspaces', 'power', 'restart_alt',
];
