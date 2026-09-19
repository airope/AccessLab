import { test } from "node:test";
import assert from "node:assert/strict";
import * as cleanup from "./cleanup.mjs";
const request = {
  id: "run",
  status: "Approved",
  operation: "Add",
  accountId: "alice",
  groupId: "analysis",
  startsAt: "2026-09-19T12:00:00Z",
  endsAt: "2026-09-20T12:00:00Z",
};
const member = { ...request, membershipId: "new", status: "Active" };
test("selects only a new exact-period membership for an approved run request", () => {
  assert.equal(typeof cleanup.selectRunMembership, "function");
  const unrelated = { ...member, membershipId: "old" };
  assert.equal(
    cleanup.selectRunMembership([unrelated, member], ["old"], request),
    member,
  );
  assert.equal(
    cleanup.selectRunMembership([unrelated], ["old"], request),
    undefined,
  );
  assert.equal(
    cleanup.selectRunMembership([member], [], {
      ...request,
      status: "Pending",
    }),
    undefined,
  );
  for (const changes of [
    { accountId: "bob" },
    { groupId: "other" },
    { endsAt: "2026-10-20T12:00:00Z" },
    { status: "Removed" },
  ])
    assert.equal(
      cleanup.selectRunMembership([{ ...member, ...changes }], [], request),
      undefined,
    );
  assert.throws(
    () =>
      cleanup.selectRunMembership(
        [member, { ...member, membershipId: "ambiguous" }],
        [],
        request,
      ),
    /ambiguous/i,
  );
});

test("cleanup distinguishes .NET ticks beyond milliseconds without mutations", async () => {
  for (const field of ["startsAt", "endsAt"]) {
    const exactRequest = {
      ...request,
      [field]: "2026-09-19T12:00:00.1234000Z",
    };
    const other = { ...member, [field]: "2026-09-19T12:00:00.1234999Z" };
    const mutations = [];
    await cleanup.cleanupRun({
      requestId: "run",
      beforeIds: [],
      get: async (path) =>
        path === "/api/requests/run"
          ? exactRequest
          : {
              page: 1,
              pageSize: 100,
              total: 1,
              items: [other],
            },
      post: async () => mutations.push("post"),
      remove: async () => mutations.push("remove"),
    });
    assert.deepEqual(mutations, []);
  }
});

test("equivalent offsets and fraction padding retain exact tick identity", () => {
  for (const [a, b] of [
    ["2026-09-19T12:00:00.1234Z", "2026-09-19T12:00:00.1234000+00:00"],
    ["2026-09-19T12:00:00Z", "2026-09-19T12:00:00.0000000-00:00"],
    ["2026-09-19T12:00:00.0000001Z", "2026-09-19T14:30:00.0000001+02:30"],
    ["2026-09-19T00:00:00.1234567Z", "2026-09-18T19:00:00.1234567-05:00"],
  ]) {
    const candidate = { ...member, startsAt: a, endsAt: a };
    assert.equal(
      cleanup.selectRunMembership([candidate], [], {
        ...request,
        startsAt: b,
        endsAt: b,
      }),
      candidate,
    );
  }
});

test("invalid timestamp responses never authorize mutations", async () => {
  for (const invalid of [
    "2026-02-30T12:00:00Z",
    "2026-09-19T24:00:00Z",
    "2026-09-19T12:00:00",
    "2026-09-19",
    "bad",
    null,
    0,
    "2026-09-19T12:00:00.12345678Z",
    "2026-09-19T12:00:00+14:01",
  ]) {
    const mutations = [];
    await cleanup.cleanupRun({
      requestId: "run",
      beforeIds: [],
      get: async (path) =>
        path === "/api/requests/run"
          ? { ...request, startsAt: invalid }
          : {
              page: 1,
              pageSize: 100,
              total: 1,
              items: [{ ...member, startsAt: invalid }],
            },
      post: async () => mutations.push("post"),
      remove: async () => mutations.push("remove"),
    });
    assert.deepEqual(mutations, [], String(invalid));
  }
});

for (const [name, pages] of [
  [
    "empty final page",
    [
      { page: 1, pageSize: 1, total: 2, items: [member] },
      { page: 2, pageSize: 1, total: 2, items: [] },
    ],
  ],
  [
    "missing final page",
    [{ page: 1, pageSize: 1, total: 2, items: [member] }, undefined],
  ],
  ["short final page", [{ page: 1, pageSize: 100, total: 2, items: [member] }]],
  [
    "changing total",
    [
      { page: 1, pageSize: 1, total: 2, items: [member] },
      {
        page: 2,
        pageSize: 1,
        total: 1,
        items: [{ ...member, membershipId: "old" }],
      },
    ],
  ],
  [
    "changing page size",
    [
      { page: 1, pageSize: 1, total: 2, items: [member] },
      {
        page: 2,
        pageSize: 2,
        total: 2,
        items: [{ ...member, membershipId: "old" }],
      },
    ],
  ],
  [
    "duplicate membership IDs",
    [
      { page: 1, pageSize: 1, total: 2, items: [member] },
      {
        page: 2,
        pageSize: 1,
        total: 2,
        items: [{ ...member, status: "Removed" }],
      },
    ],
  ],
  ["excess items", [{ page: 1, pageSize: 100, total: 0, items: [member] }]],
]) {
  test(`${name} refuses cleanup without mutations`, async () => {
    const mutations = [];
    let index = 0;
    await assert.rejects(
      cleanup.cleanupRun({
        requestId: "run",
        beforeIds: ["old"],
        get: async (path) =>
          path === "/api/requests/run" ? request : pages[index++],
        post: async () => mutations.push("post"),
        remove: async () => mutations.push("remove"),
      }),
      /membership pagination/i,
    );
    assert.deepEqual(mutations, []);
  });
}

test("valid empty results and a partial-sized final page are complete", async () => {
  assert.deepEqual(
    await cleanup.readMemberships(
      async () => ({
        page: 1,
        pageSize: 100,
        total: 0,
        items: [],
      }),
      "alice",
      "analysis",
    ),
    [],
  );
  const rows = [
    member,
    { ...member, membershipId: "second" },
    { ...member, membershipId: "third" },
  ];
  let page = 0;
  assert.deepEqual(
    await cleanup.readMemberships(
      async () => {
        page++;
        return {
          page,
          pageSize: 2,
          total: 3,
          items: rows.slice((page - 1) * 2, page * 2),
        };
      },
      "alice",
      "analysis",
    ),
    rows,
  );
  assert.equal(page, 2);
});

test("reads every scoped page before choosing, never just the first active row", async () => {
  assert.equal(typeof cleanup.readMemberships, "function");
  const paths = [];
  const result = await cleanup.readMemberships(
    async (path) => {
      paths.push(path);
      const q = new URL(path, "https://test.invalid").searchParams;
      assert.equal(q.get("accountId"), "alice");
      assert.equal(q.get("groupId"), "analysis");
      const page = Number(q.get("page"));
      return {
        items: [{ ...member, membershipId: String(page) }],
        page,
        pageSize: 1,
        total: 3,
      };
    },
    "alice",
    "analysis",
  );
  assert.deepEqual(
    result.map((m) => m.membershipId),
    ["1", "2", "3"],
  );
  assert.equal(paths.length, 3);
});
test("pending run is cancelled explicitly without membership lookup or removal", async () => {
  assert.equal(typeof cleanup.cleanupRun, "function");
  const posts = [];
  await cleanup.cleanupRun({
    requestId: "run",
    beforeIds: [],
    get: async (path) => {
      assert.equal(path, "/api/requests/run");
      return { ...request, status: "Pending" };
    },
    post: async (path, body) => posts.push([path, body]),
    remove: async () => assert.fail("must not remove"),
  });
  assert.equal(posts[0][0], "/api/requests/run/cancel");
});
test("all contexts close even when one close fails", async () => {
  assert.equal(typeof cleanup.closeAll, "function");
  const closed = [];
  await assert.rejects(
    cleanup.closeAll([
      {
        close: async () => {
          closed.push(1);
          throw Error("close");
        },
      },
      {
        close: async () => {
          closed.push(2);
        },
      },
    ]),
  );
  assert.deepEqual(closed, [1, 2]);
});
test("mutation authorization is explicit, not implied by localhost", () => {
  const previous = process.env.ACCESSLAB_E2E_ISOLATED;
  delete process.env.ACCESSLAB_E2E_ISOLATED;
  try {
    for (const value of [undefined, "", "0", "true"])
      assert.throws(
        () => cleanup.requireIsolated(value),
        /ACCESSLAB_E2E_ISOLATED=1/,
      );
    assert.doesNotThrow(() => cleanup.requireIsolated("1"));
  } finally {
    if (previous !== undefined) process.env.ACCESSLAB_E2E_ISOLATED = previous;
  }
});
test("approved cleanup removes only its exact new membership on a later page", async () => {
  const removed = [];
  await cleanup.cleanupRun({
    requestId: "run",
    beforeIds: ["old"],
    get: async (path) => {
      if (path === "/api/requests/run") return request;
      const page = Number(
        new URL(path, "https://test.invalid").searchParams.get("page"),
      );
      return {
        page,
        pageSize: 1,
        total: 2,
        items: [page === 1 ? { ...member, membershipId: "old" } : member],
      };
    },
    post: async () => assert.fail("approved request must not be cancelled"),
    remove: async (m) => {
      removed.push(m.membershipId);
    },
  });
  assert.deepEqual(removed, ["new"]);
});
test("cleanup never removes unrelated grants if the approved grant is absent", async () => {
  await cleanup.cleanupRun({
    requestId: "run",
    beforeIds: ["old"],
    get: async (path) =>
      path === "/api/requests/run"
        ? request
        : {
            page: 1,
            pageSize: 100,
            total: 2,
            items: [
              { ...member, membershipId: "old" },
              { ...member, accountId: "bob" },
            ],
          },
    post: async () => assert.fail("no cancellation"),
    remove: async () => assert.fail("no removal"),
  });
});
test("incomplete pagination refuses cleanup rather than acting on partial results", async () => {
  await assert.rejects(
    cleanup.readMemberships(
      async () => ({ page: 1, pageSize: 1, total: 2, items: [] }),
      "alice",
      "analysis",
    ),
    /Incomplete/,
  );
});
test("contexts close after cleanup read failure without a mutation", async () => {
  let closed = false;
  await assert.rejects(
    (async () => {
      try {
        await cleanup.cleanupRun({
          requestId: "run",
          beforeIds: [],
          get: async () => {
            throw Error("read failed");
          },
          post: async () => assert.fail("no mutation"),
          remove: async () => assert.fail("no removal"),
        });
      } finally {
        await cleanup.closeAll([
          {
            close: async () => {
              closed = true;
            },
          },
        ]);
      }
    })(),
    /read failed/,
  );
  assert.equal(closed, true);
});
