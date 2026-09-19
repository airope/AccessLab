export interface Session {
  maxMembershipDays?: number;
  accountId: string;
  displayName: string;
  status: string;
  roles: { role: string; projectId: string | null }[];
  projectIds: string[];
  demoMode: boolean;
}
export interface Page<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}
export interface Account {
  startsAt?: string | null;
  endsAt?: string | null;
  id: string;
  displayName: string;
  status: "Active" | "Suspended" | "Ended";
  projectIds: string[];
  revision: number;
}
export interface Group {
  id: string;
  name: string;
  projectId: string;
  description: string;
  requestable: boolean;
  resourceIds: string[];
}
export interface Membership {
  membershipId: string;
  groupId?: string;
  accountId: string;
  displayName: string;
  startsAt: string;
  endsAt: string;
  status: string;
}
export interface AccessReason {
  groupId: string;
  groupName: string;
  startsAt: string;
  endsAt: string;
}
export interface Resource {
  ruleDescription?: string | null;
  denialReason?: string | null;
  id: string;
  name: string;
  projectId: string;
  description: string;
  allowed: boolean;
  observed: boolean;
  effective: boolean;
  reasons: AccessReason[];
  ruleVersion: number;
}
export interface Access {
  accountId: string;
  accountName: string;
  resources: Resource[];
  sync: { status: string; desiredRevision: number; observedRevision: number };
}
export interface Change {
  resourceId: string;
  resourceName: string;
  effect: "Added" | "Removed" | "Retained" | "Unchanged";
  reasons: string[];
}
export interface Preview {
  id: string;
  expiresAt: string;
  accountRevision: number;
  requestId?: string;
  changes: Change[];
  scheduledChanges: { at: string; changes: Change[] }[];
  warnings: string[];
}
export interface ChangeCommand {
  accountId: string;
  groupId: string;
  operation: string;
  startsAt: string;
  endsAt: string;
  reason: string;
  membershipId?: string | null;
}
export interface DecisionContext {
  accountRevision: number;
  ruleVersion: number;
  fingerprint: string;
  calculatedAt: string;
  expiresAt: string;
  command: ChangeCommand;
}
export interface AccessRequest {
  acceptedPreviewId?: string | null;
  decisionContext?: DecisionContext | null;
  id: string;
  accountId: string;
  accountName: string;
  actorId: string;
  actorName: string;
  groupId: string;
  groupName: string;
  projectId: string;
  operation: "Add" | "Remove";
  startsAt: string;
  endsAt: string;
  reason: string;
  status: string;
  createdAt: string;
  expiresAt: string;
  decisionReason: string | null;
  decidedBy: string | null;
  correlationId: string;
  canReview: boolean;
  canCancel: boolean;
  syncStatus: string | null;
}
export interface SyncAttempt {
  id: number;
  accountId: string;
  revision: number;
  attemptedAt: string;
  outcome: string;
  error: string | null;
}
export interface Operation {
  accountId: string;
  accountName: string;
  status: string;
  desiredRevision: number;
  observedRevision: number;
  attempts: number;
  lastError: string | null;
  updatedAt: string;
}
export interface AuditEvent {
  accountRevision?: number;
  id: number;
  at: string;
  actorName: string;
  action: string;
  targetId: string;
  projectId: string | null;
  outcome: string;
  reason: string;
  correlationId: string;
}
export function hasRole(session: Session, role: string, projectId?: string) {
  return session.roles.some(
    (r) => r.role === role && (!projectId || r.projectId === projectId),
  );
}
