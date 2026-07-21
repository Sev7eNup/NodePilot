import { useTranslation } from 'react-i18next';
import type { ConfigProps } from '../shared';
import { FsOperationConfig } from './fsOperationShared';

export function FolderOperationConfig(props: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const FOLDER_OPERATIONS = [
    { value: 'copy',   label: t('config.folderOperation.operationCopy') },
    { value: 'move',   label: t('config.folderOperation.operationMove') },
    { value: 'rename', label: t('config.folderOperation.operationRename') },
    { value: 'delete', label: t('config.folderOperation.operationDelete') },
    { value: 'exists', label: t('config.folderOperation.operationExists') },
    { value: 'list',   label: t('config.folderOperation.operationList') },
    { value: 'create', label: t('config.folderOperation.operationCreate') },
  ];
  return (
    <FsOperationConfig
      {...props}
      operations={FOLDER_OPERATIONS}
      pathLabel={t('config.fsShared.pathFolder')}
      pathPlaceholder="C:\Temp\Folder"
      destinationPlaceholder="D:\Backup\Folder"
      newNamePlaceholder="RenamedFolder"
    />
  );
}
