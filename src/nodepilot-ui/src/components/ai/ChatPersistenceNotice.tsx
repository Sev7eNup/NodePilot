import { useTranslation } from 'react-i18next';
import { useChatPersistenceStatus } from '../../stores/chatPersistenceStatus';

export function ChatPersistenceNotice() {
  const { t } = useTranslation('ai');
  const unavailable = useChatPersistenceStatus((state) => state.unavailable);
  const trimmed = useChatPersistenceStatus((state) => state.trimmed);
  if (!unavailable && !trimmed) return null;
  return <p role="status" className="px-3 py-2 text-xs text-on-warning-container bg-warning-container/30">
    {t(unavailable ? 'chat.persistenceUnavailable' : 'chat.persistenceTrimmed')}
  </p>;
}
