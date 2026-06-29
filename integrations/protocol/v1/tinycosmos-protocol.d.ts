export type TinyCosmosOperation =
  | "Capabilities"
  | "GetOrCreateGroup"
  | "CreateGroup"
  | "ListGroups"
  | "InspectGroup"
  | "UpdateGroupMetadata"
  | "DeleteGroup"
  | "ForkGroup"
  | "ReplacePrimarySandbox"
  | "StartSandbox"
  | "StopSandbox"
  | "Exec"
  | "FileRead"
  | "FileWrite"
  | "FileList"
  | "FileSearch"
  | "Diagnostics";

export interface ProtocolEnvelope<TPayload> {
  version: 1;
  requestId: string;
  operation: TinyCosmosOperation;
  payload: TPayload;
}

export interface ProtocolResponse<TPayload> {
  version: 1;
  requestId: string;
  success: boolean;
  payload?: TPayload;
  error?: TinyCosmosError;
}

export interface TinyCosmosError {
  code: string;
  message: string;
  remediation?: string;
}

export interface ResourceAllocation {
  vcpu: number;
  memoryMiB: number;
  systemDiskGiB: number;
  workspaceDiskGiB: number;
}

export interface GroupCreatePayload {
  logicalName: string;
  ownerUid: number;
  hostWorkspacePath?: string | null;
  guestProjectPath?: string | null;
  imageId: string;
  resources: ResourceAllocation;
}

export interface ExecPayload {
  ownerUid: number;
  groupId: string;
  workingDirectory: string;
  command: string[];
  timeoutSeconds: number;
  outputLimitBytes: number;
  pseudoTerminal: boolean;
}

export interface ForkGroupPayload {
  ownerUid: number;
  sourceGroupId: string;
  newLogicalName: string;
}

export interface ReplacePrimarySandboxPayload {
  ownerUid: number;
  groupId: string;
  imageId?: string | null;
  resources?: ResourceAllocation | null;
}

export interface FileReadPayload {
  ownerUid: number;
  groupId: string;
  path: string;
  maxBytes: number;
}

export interface GuestReadyReport {
  bootNonce: string;
  sshUser: string;
  sshHostKey?: string | null;
  sudoAvailable: boolean;
  dockerAvailable: boolean;
  reportedAt: string;
}

export interface DiagnosticCheckPayload {
  name: string;
  passed: boolean;
  message: string;
  remediation?: string | null;
}

export interface ManagerDiagnosticsResponse {
  passed: boolean;
  checks: DiagnosticCheckPayload[];
}
