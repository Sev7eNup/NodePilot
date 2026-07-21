import { api } from './client';

/** Host identity of the API server, shown in the app header. See SystemController. */
export type HostInfo = {
  machineName: string;
  fqdn: string;
  domain: string | null;
};

export const systemApi = {
  getHostInfo: () => api.get<HostInfo>('/system/host-info'),
};
