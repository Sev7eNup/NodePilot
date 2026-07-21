import { useTranslation } from 'react-i18next';
import type { ConfigProps } from '../shared';
import { FsOperationConfig } from './fsOperationShared';

export function FileOperationConfig(props: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const FILE_OPERATIONS = [
    { value: 'copy',   label: t('config.fileOperation.operationCopy') },
    { value: 'move',   label: t('config.fileOperation.operationMove') },
    { value: 'rename', label: t('config.fileOperation.operationRename') },
    { value: 'delete', label: t('config.fileOperation.operationDelete') },
    { value: 'exists', label: t('config.fileOperation.operationExists') },
    { value: 'create', label: t('config.fileOperation.operationCreate') },
  ];
  return (
    <FsOperationConfig
      {...props}
      operations={FILE_OPERATIONS}
      pathLabel={t('config.fsShared.pathFile')}
      pathPlaceholder="C:\Temp\file.txt"
      destinationPlaceholder="D:\Backup\file.txt"
      newNamePlaceholder="renamed.txt"
    />
  );
}
