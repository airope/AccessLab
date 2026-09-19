export function requireIsolated(value = process.env.ACCESSLAB_E2E_ISOLATED) {
  if (value !== "1")
    throw new Error(
      "Refusing E2E/demo mutations: use an isolated database and ACCESSLAB_E2E_ISOLATED=1",
    );
}

export async function closeAll(contexts) {
  const results = await Promise.allSettled(
    contexts.map((context) => context.close()),
  );
  const errors = results
    .filter((result) => result.status === "rejected")
    .map((result) => result.reason);
  if (errors.length) throw new AggregateError(errors, "Context closure failed");
}

export async function readMemberships(get, accountId, groupId) {
  const items = [];
  const seenIds = new Set();
  let total;
  let pageSize;
  for (let page = 1; ; page++) {
    const query = new URLSearchParams({
      accountId,
      groupId,
      page: String(page),
      pageSize: "100",
    });
    const result = await get(`/api/memberships?${query}`);
    if (
      !result ||
      result.page !== page ||
      !Number.isSafeInteger(result.total) ||
      result.total < 0 ||
      !Number.isSafeInteger(result.pageSize) ||
      result.pageSize < 1 ||
      !Array.isArray(result.items) ||
      (page > 1 && (result.total !== total || result.pageSize !== pageSize))
    )
      throw new Error("Invalid membership pagination; refusing cleanup");
    total = result.total;
    pageSize = result.pageSize;
    const expectedCount = Math.min(pageSize, total - items.length);
    if (result.items.length !== expectedCount)
      throw new Error("Incomplete membership pagination; refusing cleanup");
    for (const item of result.items) {
      if (
        !item ||
        typeof item.membershipId !== "string" ||
        !item.membershipId ||
        seenIds.has(item.membershipId)
      )
        throw new Error(
          "Invalid membership pagination identity; refusing cleanup",
        );
      seenIds.add(item.membershipId);
    }
    items.push(...result.items);
    if (items.length === total) return items;
  }
}

export async function cleanupRun({ requestId, beforeIds, get, post, remove }) {
  if (!requestId) return;
  const request = await get(`/api/requests/${requestId}`);
  if (request.id !== requestId)
    throw new Error("Cleanup request identity mismatch");
  if (request.status === "Pending") {
    await post(`/api/requests/${requestId}/cancel`, {
      reason: "Run-owned acceptance cleanup",
    });
    return;
  }
  // Authoritative status also covers approval committed before a UI assertion failed.
  if (request.status !== "Approved" || request.operation !== "Add") return;
  const items = await readMemberships(get, request.accountId, request.groupId);
  const membership = selectRunMembership(items, beforeIds, request);
  if (membership) await remove(membership);
}

// Parse the supported .NET timestamp form without rounding away 100 ns ticks.
// Explicit zones and calendar validation prevent Date.parse's lenient rollovers.
function instantTicks(value) {
  if (typeof value !== "string") return;
  const match =
    /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|([+-])(\d{2}):(\d{2}))$/.exec(
      value,
    );
  if (!match || match[1].startsWith("0000")) return;
  const base = `${match[1]}T${match[2]}`;
  const milliseconds = Date.parse(`${base}Z`);
  if (
    !Number.isFinite(milliseconds) ||
    new Date(milliseconds).toISOString() !== `${base}.000Z`
  )
    return;
  const hours = Number(match[6] ?? 0);
  const minutes = Number(match[7] ?? 0);
  if (hours > 14 || minutes > 59 || (hours === 14 && minutes !== 0)) return;
  const offsetSeconds =
    (hours * 60 + minutes) * 60 * (match[5] === "-" ? -1 : 1);
  return (
    BigInt(milliseconds) * 10000n -
    BigInt(offsetSeconds) * 10000000n +
    BigInt((match[3] ?? "").padEnd(7, "0"))
  );
}

function sameTime(a, b) {
  const ticks = instantTicks(a);
  return ticks !== undefined && ticks === instantTicks(b);
}

// Membership DTO does not expose its internal RequestId. Require both a
// pre-submit snapshot difference and the persisted approved request's period.
export function selectRunMembership(items, beforeIds, request) {
  if (request.status !== "Approved" || request.operation !== "Add") return;
  const before = new Set(beforeIds);
  const candidates = items.filter(
    (m) =>
      !before.has(m.membershipId) &&
      m.status === "Active" &&
      m.accountId === request.accountId &&
      m.groupId === request.groupId &&
      sameTime(m.startsAt, request.startsAt) &&
      sameTime(m.endsAt, request.endsAt),
  );
  if (candidates.length > 1)
    throw new Error("Ambiguous run-owned membership; refusing cleanup");
  return candidates[0];
}
