/**
 * Stable re-export boundary for backend enums.
 *
 * Generated from the backend OpenAPI document via `npm run codegen`.
 * Never hand-write backend enum values — re-export the generated enum here
 * and import it from '@/lib/enums' in route code.
 */
export {
  AiActorExecutionPhase,
  AiActorStatus,
  AiProvider,
  AnomalyDetectionMethod,
  AnomalySensitivity,
  ApprovalStatus,
  BackgroundTaskState,
  BedrockAuthMode,
  ChangeApprovalKind,
  ChangeSource,
  DataContractRuleType,
  DataContractSeverity,
  DatabaseEngineType,
  DataSourceType,
  FileType,
  GroundingRecheckKind,
  HealthStatus,
  McpDocPatchStatus,
  McpEvalFailureTag,
  McpDocPatchTarget,
  McpPatternStatus,
  McpPatternType,
  MigrationMode,
  MigrationStatus,
  NotificationStatus,
  NotificationTrigger,
  NotificationType,
  OwnedResourceKind,
  ParameterType,
  ProjectMembershipManagement,
  ProjectRole,
  QueryVersionStatus,
  TaskPriority,
} from '@/api/generated/beacon-api';
