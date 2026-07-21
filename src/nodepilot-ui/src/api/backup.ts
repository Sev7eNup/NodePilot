import { api, downloadFromApiPost } from './client';

// Mirrors src/NodePilot.Api/Dtos/BackupDtos.cs + the restore service result records — part
// of the system-configuration backup/restore feature (design decision ADR 0001).
export interface BackupSectionCount {
  section: string;
  count: number;
}
export interface BackupManifest {
  sections: BackupSectionCount[];
}

export interface BackupPreviewSection {
  section: string;
  inBackup: number;
  new: number;
  conflicts: number;
}
export interface BackupPreviewResult {
  integrityVerified: boolean;
  appVersion: string | null;
  sections: BackupPreviewSection[];
  warnings: string[];
}

export type RestorePolicy = 'skip' | 'rename' | 'overwrite';

export interface SectionRestoreResult {
  section: string;
  created: number;
  overwritten: number;
  skipped: number;
  renamed: number;
}
export interface SettingsRestoreResult {
  applied: boolean;
  message: string | null;
}
export interface BackupRestoreResult {
  sections: SectionRestoreResult[];
  settings: SettingsRestoreResult | null;
  warnings: string[];
}

/** Section keys, in display order — must match BackupSections on the server. */
export const BACKUP_SECTIONS = [
  'folders',
  'users',
  'credentials',
  'machines',
  'globalVariables',
  'workflows',
  'settings',
] as const;

export const backupApi = {
  getManifest: () => api.get<BackupManifest>('/backup/manifest'),

  /** POSTs the export request and triggers a browser download of the sealed .npbackup. */
  download: (sections: string[], passphrase: string) =>
    downloadFromApiPost(
      '/backup/export',
      { sections, passphrase },
      `nodepilot-backup-${new Date().toISOString().slice(0, 10)}.npbackup`,
    ),

  preview: (file: File, passphrase: string) => {
    const form = new FormData();
    form.append('file', file);
    if (passphrase) form.append('passphrase', passphrase);
    return api.postForm<BackupPreviewResult>('/backup/preview', form);
  },

  restore: (file: File, passphrase: string, policy: string) => {
    const form = new FormData();
    form.append('file', file);
    form.append('passphrase', passphrase);
    if (policy) form.append('policy', policy);
    return api.postForm<BackupRestoreResult>('/backup/restore', form);
  },
};
