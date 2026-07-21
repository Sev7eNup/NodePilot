import type { ComponentType } from 'react';
import { ACTIVITY_TYPES } from '../../../lib/activityTypes';
import { isCustomActivityType } from '../../../lib/customActivities';
import type { ConfigProps } from './shared';
import { DynamicActivityConfig } from './activities/DynamicActivityConfig';

import { RunScriptConfig } from './activities/RunScriptConfig';
import { FileOperationConfig } from './activities/FileOperationConfig';
import { FolderOperationConfig } from './activities/FolderOperationConfig';
import { FileHashConfig } from './activities/FileHashConfig';
import { ZipOperationConfig } from './activities/ZipOperationConfig';
import { ServiceManagementConfig } from './activities/ServiceManagementConfig';
import { ScheduledTaskConfig } from './activities/ScheduledTaskConfig';
import { RegistryConfig } from './activities/RegistryConfig';
import { WmiQueryConfig } from './activities/WmiQueryConfig';
import { StartProgramConfig } from './activities/StartProgramConfig';
import { PowerManagementConfig } from './activities/PowerManagementConfig';
import { WaitForConditionConfig } from './activities/WaitForConditionConfig';
import { RestApiConfig } from './activities/RestApiConfig';
import { SqlConfig } from './activities/SqlConfig';
import { EmailConfig } from './activities/EmailConfig';
import { TextFileEditConfig } from './activities/TextFileEditConfig';
import { DelayConfig } from './activities/DelayConfig';
import { GenerateTextConfig } from './activities/GenerateTextConfig';
import { LlmQueryConfig } from './activities/LlmQueryConfig';
import { XmlQueryConfig } from './activities/XmlQueryConfig';
import { JsonQueryConfig } from './activities/JsonQueryConfig';
import { LogConfig } from './activities/LogConfig';
import { JunctionConfig } from './activities/JunctionConfig';
import { StartWorkflowConfig } from './activities/StartWorkflowConfig';
import { ForEachConfig } from './activities/ForEachConfig';
import { DecisionConfig } from './activities/DecisionConfig';
import { ReturnDataConfig } from './activities/ReturnDataConfig';
import { ScheduleTriggerConfig } from './triggers/ScheduleTriggerConfig';
import { WebhookTriggerConfig } from './triggers/WebhookTriggerConfig';
import { FileWatcherTriggerConfig } from './triggers/FileWatcherTriggerConfig';
import { DatabaseTriggerConfig } from './triggers/DatabaseTriggerConfig';
import { EventLogTriggerConfig } from './triggers/EventLogTriggerConfig';
import { ManualTriggerConfig } from './triggers/ManualTriggerConfig';

// Routing from activityType → its config-editor component. Keeps PropertiesPanel.tsx
// free of the 26-branch if-cascade. Adding a new activity = one line here.
export const ACTIVITY_CONFIG_COMPONENTS: Record<string, ComponentType<ConfigProps>> = {
  [ACTIVITY_TYPES.RUN_SCRIPT]: RunScriptConfig,
  [ACTIVITY_TYPES.FILE_OPERATION]: FileOperationConfig,
  [ACTIVITY_TYPES.FOLDER_OPERATION]: FolderOperationConfig,
  [ACTIVITY_TYPES.FILE_HASH]: FileHashConfig,
  [ACTIVITY_TYPES.ZIP_OPERATION]: ZipOperationConfig,
  [ACTIVITY_TYPES.SERVICE_MANAGEMENT]: ServiceManagementConfig,
  [ACTIVITY_TYPES.SCHEDULED_TASK]: ScheduledTaskConfig,
  [ACTIVITY_TYPES.REGISTRY_OPERATION]: RegistryConfig,
  [ACTIVITY_TYPES.WMI_QUERY]: WmiQueryConfig,
  [ACTIVITY_TYPES.START_PROGRAM]: StartProgramConfig,
  [ACTIVITY_TYPES.POWER_MANAGEMENT]: PowerManagementConfig,
  [ACTIVITY_TYPES.WAIT_FOR_CONDITION]: WaitForConditionConfig,
  [ACTIVITY_TYPES.REST_API]: RestApiConfig,
  [ACTIVITY_TYPES.SQL]: SqlConfig,
  [ACTIVITY_TYPES.EMAIL_NOTIFICATION]: EmailConfig,
  [ACTIVITY_TYPES.TEXT_FILE_EDIT]: TextFileEditConfig,
  [ACTIVITY_TYPES.DELAY]: DelayConfig,
  [ACTIVITY_TYPES.GENERATE_TEXT]: GenerateTextConfig,
  [ACTIVITY_TYPES.LLM_QUERY]: LlmQueryConfig,
  [ACTIVITY_TYPES.XML_QUERY]: XmlQueryConfig,
  [ACTIVITY_TYPES.JSON_QUERY]: JsonQueryConfig,
  [ACTIVITY_TYPES.LOG]: LogConfig,
  [ACTIVITY_TYPES.JUNCTION]: JunctionConfig,
  [ACTIVITY_TYPES.START_WORKFLOW]: StartWorkflowConfig,
  [ACTIVITY_TYPES.FOR_EACH]: ForEachConfig,
  [ACTIVITY_TYPES.DECISION]: DecisionConfig,
  [ACTIVITY_TYPES.RETURN_DATA]: ReturnDataConfig,
  [ACTIVITY_TYPES.SCHEDULE_TRIGGER]: ScheduleTriggerConfig,
  [ACTIVITY_TYPES.WEBHOOK_TRIGGER]: WebhookTriggerConfig,
  [ACTIVITY_TYPES.FILE_WATCHER_TRIGGER]: FileWatcherTriggerConfig,
  [ACTIVITY_TYPES.DATABASE_TRIGGER]: DatabaseTriggerConfig,
  [ACTIVITY_TYPES.EVENT_LOG_TRIGGER]: EventLogTriggerConfig,
  [ACTIVITY_TYPES.MANUAL_TRIGGER]: ManualTriggerConfig,
};

/**
 * Resolves the config-editor component for an activity type. Built-ins come from the static map;
 * every custom:<key> activity falls back to the schema-driven {@link DynamicActivityConfig}.
 */
export function getActivityConfigComponent(activityType: string): ComponentType<ConfigProps> | undefined {
  return ACTIVITY_CONFIG_COMPONENTS[activityType] ?? (isCustomActivityType(activityType) ? DynamicActivityConfig : undefined);
}
