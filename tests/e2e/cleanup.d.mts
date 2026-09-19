export interface Membership {
  membershipId: string;
  accountId: string;
  groupId: string;
  status: string;
  startsAt: string;
  endsAt: string;
}
export interface RunRequest extends Omit<Membership, "membershipId"> {
  id: string;
  operation: string;
}
export type Get = (path: string) => Promise<unknown>;
export function requireIsolated(value?: string): void;
export function closeAll(contexts: { close(): Promise<void> }[]): Promise<void>;
export function readMemberships(
  get: Get,
  accountId: string,
  groupId: string,
): Promise<Membership[]>;
export function selectRunMembership(
  items: Membership[],
  beforeIds: string[],
  request: RunRequest,
): Membership | undefined;
export function cleanupRun(options: {
  requestId?: string;
  beforeIds: string[];
  get: Get;
  post: (path: string, data: unknown) => Promise<unknown>;
  remove: (membership: Membership) => Promise<void>;
}): Promise<void>;
