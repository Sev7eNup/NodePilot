import { createContext } from 'react';
import type { MachineOption, Credential } from '../../../types/api';

/** Node-level bindings reuse the editor's dirty/undo path; member bindings live in config. */
export const AgentAuthoringContext = createContext<{
  data: Record<string, unknown>;
  update: (patch: Record<string, unknown>) => void;
  machines: MachineOption[];
  credentials: Credential[];
}>({ data: {}, update: () => {}, machines: [], credentials: [] });
