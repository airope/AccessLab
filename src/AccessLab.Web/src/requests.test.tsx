import { it, expect, vi, beforeEach, afterEach } from "vitest";
beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date("2026-09-18T10:00:00Z"));
});
afterEach(() => vi.useRealTimers());
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { RequestForm, RequestReview, RequestsPage } from "./requests";
import { api, ApiError } from "./api";
import type {
  Account,
  AccessRequest,
  Group,
  Page,
  Preview,
  Session,
} from "./types";

it.each(["preview", "approve", "reject", "cancel"])(
  "reloads terminal request after a competing reviewer wins during %s",
  async (action) => {
    const pending = {
      id: "r1",
      accountId: "a",
      accountName: "Alice",
      actorId: "a",
      actorName: "Alice",
      groupId: "analysis",
      groupName: "Analysis",
      projectId: "atlas",
      operation: "Add",
      startsAt: "2026-09-20T10:00:00Z",
      endsAt: "2026-10-01T10:00:00Z",
      reason: "Research period",
      status: "Pending",
      createdAt: "2026-09-18T10:00:00Z",
      expiresAt: "2026-10-01T10:00:00Z",
      decisionReason: null,
      decidedBy: null,
      correlationId: "c-1",
      canReview: true,
      canCancel: true,
      syncStatus: null,
    } satisfies AccessRequest;
    const terminal = {
      ...pending,
      status: "Rejected",
      decisionReason: "Other reviewer decided",
      decidedBy: "reviewer-a",
      canReview: false,
      canCancel: false,
    } satisfies AccessRequest;
    const get = vi
      .spyOn(api, "get")
      .mockResolvedValueOnce(pending)
      .mockResolvedValue(terminal);
    const post = vi.spyOn(api, "post");
    if (action === "approve") post.mockResolvedValueOnce(preview);
    post.mockRejectedValue(
      new ApiError(409, "Request already decided", "c-2", "request_terminal"),
    );
    render(<RequestReview id="r1" />);
    await screen.findByRole("button", { name: "Preview impact" });
    if (action === "preview" || action === "approve") {
      fireEvent.click(screen.getByRole("button", { name: "Preview impact" }));
      if (action === "approve")
        fireEvent.click(await screen.findByRole("button", { name: "Approve" }));
    } else {
      fireEvent.click(
        screen.getByRole("button", {
          name: action === "reject" ? "Reject" : "Cancel request",
        }),
      );
      fireEvent.change(screen.getByLabelText("Reason"), {
        target: { value: "My decision" },
      });
      fireEvent.click(
        screen.getByRole("button", {
          name:
            action === "reject" ? "Confirm rejection" : "Confirm cancellation",
        }),
      );
    }
    await screen.findByRole("heading", { name: "Decision recorded" });
    expect(get).toHaveBeenCalledTimes(2);
    expect(get.mock.calls[1][0]).toBe("/api/requests/r1");
    expect(screen.getByText("Other reviewer decided")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Refresh impact" })).toBeNull();
    expect(screen.queryByLabelText("Reason")).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
    expect(post).toHaveBeenCalledTimes(action === "approve" ? 2 : 1);
  },
);

it("keeps the displayed beneficiary and project scope aligned with preview across account pages", async () => {
  const bob = {
    id: "b",
    displayName: "Bob",
    status: "Active",
    projectIds: ["atlas"],
    revision: 1,
  } satisfies Account;
  const carol = {
    ...bob,
    id: "c",
    displayName: "Carol",
    projectIds: ["other"],
  } satisfies Account;
  const groups = {
    items: [
      {
        id: "analysis",
        name: "Analysis",
        projectId: "atlas",
        description: "Research",
        requestable: true,
        resourceIds: [],
      },
      {
        id: "other",
        name: "Other project",
        projectId: "other",
        description: "Other",
        requestable: true,
        resourceIds: [],
      },
    ],
  } satisfies { items: Group[] };
  vi.spyOn(api, "get").mockImplementation(async (path) =>
    path.includes("groups")
      ? groups
      : ({
          items: path.includes("page=2") ? [carol] : [bob],
          total: 101,
          page: path.includes("page=2") ? 2 : 1,
          pageSize: 100,
        } satisfies Page<Account>),
  );
  const post = vi.spyOn(api, "post").mockResolvedValue({
    id: "p",
    expiresAt: "2099-01-01T00:00:00Z",
    accountRevision: 1,
    changes: [],
    scheduledChanges: [],
    warnings: [],
  } satisfies Preview);
  const reviewer = {
    ...session,
    roles: [{ role: "Reviewer", projectId: "atlas" }],
  } satisfies Session;
  render(<RequestForm session={reviewer} />);
  await screen.findByRole("option", { name: "Bob" });
  fireEvent.change(screen.getByLabelText("Beneficiary"), {
    target: { value: "b" },
  });
  fireEvent.change(screen.getByLabelText("Group"), {
    target: { value: "analysis" },
  });
  fireEvent.change(screen.getByLabelText("Reason"), {
    target: { value: "Research access" },
  });
  fireEvent.change(screen.getByLabelText("End date"), {
    target: { value: "2026-09-25T10:00" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Next" }));
  await screen.findByRole("option", { name: "Carol" });
  const beneficiary = screen.getByLabelText<HTMLSelectElement>("Beneficiary");
  expect(beneficiary.value).toBe("b");
  expect(beneficiary.selectedOptions[0].textContent).toBe("Bob");
  expect(screen.queryByRole("option", { name: /Other project/ })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Preview impact" }));
  await screen.findByRole("button", { name: "Submit request" });
  expect(post.mock.calls[0][1]).toMatchObject({
    accountId: beneficiary.value,
    groupId: "analysis",
  });
});
const session = {
  accountId: "a",
  displayName: "Alice",
  status: "Active",
  roles: [{ role: "Member", projectId: "atlas" }],
  projectIds: ["atlas"],
  demoMode: true,
};
it("selects an exact own membership for removal without reading a group directory", async () => {
  const get = vi.spyOn(api, "get").mockImplementation(async (path) =>
    path.startsWith("/api/memberships")
      ? {
          total: 26,
          page: 1,
          pageSize: 25,
          items: [
            {
              membershipId: "m1",
              accountId: "a",
              displayName: "Alice",
              groupId: "analysis",
              startsAt: "2026-10-01T00:00:00Z",
              endsAt: "2026-10-20T00:00:00Z",
              status: "Active",
            },
          ],
        }
      : path.includes("groups")
        ? {
            items: [
              {
                id: "analysis",
                name: "Analysis",
                projectId: "atlas",
                description: "Research",
                requestable: true,
                resourceIds: ["results"],
              },
            ],
          }
        : ({ items: [], total: 0, page: 1, pageSize: 100 } as never),
  );
  render(<RequestForm session={session} />);
  await screen.findByRole("option", { name: /Analysis/ });
  fireEvent.change(screen.getByLabelText("Operation"), {
    target: { value: "Remove" },
  });
  fireEvent.change(screen.getByLabelText("Group"), {
    target: { value: "analysis" },
  });
  await screen.findByRole("option", { name: /Alice ·/ });
  expect(
    get.mock.calls.some(
      ([path]) =>
        path ===
        "/api/memberships?accountId=a&groupId=analysis&page=1&pageSize=25",
    ),
  ).toBe(true);
  expect(
    get.mock.calls.some(([path]) => path.includes("/groups/analysis/members")),
  ).toBe(false);
  fireEvent.change(screen.getByLabelText("Membership"), {
    target: { value: "m1" },
  });
  expect(
    screen.getByRole("button", { name: "Next" }).getAttribute("type"),
  ).toBe("button");
  fireEvent.click(screen.getByRole("button", { name: "Next" }));
  await waitFor(() =>
    expect(
      get.mock.calls.some(
        ([path]) =>
          path ===
          "/api/memberships?accountId=a&groupId=analysis&page=2&pageSize=25",
      ),
    ).toBe(true),
  );
  await screen.findByLabelText("Membership");
  expect((screen.getByLabelText("Membership") as HTMLSelectElement).value).toBe(
    "",
  );
  expect(screen.queryByLabelText("Membership ID (optional)")).toBeNull();
  expect(screen.queryByLabelText("When should this change start?")).toBeNull();
});
it("preserves the group deep link through server-side search and pagination", async () => {
  history.replaceState({}, "", "/requests?groupId=analysis");
  const get = vi
    .spyOn(api, "get")
    .mockResolvedValue({ items: [], total: 26, page: 1, pageSize: 25 });
  render(<RequestsPage />);
  fireEvent.click(await screen.findByRole("button", { name: "Next" }));
  await waitFor(() =>
    expect(
      get.mock.calls.some(
        ([p]) => p.includes("groupId=analysis") && p.includes("page=2"),
      ),
    ).toBe(true),
  );
  fireEvent.change(screen.getByLabelText("Search requests"), {
    target: { value: "research" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() =>
    expect(
      get.mock.calls.some(
        ([p]) =>
          p.includes("groupId=analysis") &&
          p.includes("search=research") &&
          p.includes("page=1"),
      ),
    ).toBe(true),
  );
  history.replaceState({}, "", "/");
});
const preview = {
  id: "server-preview",
  expiresAt: "2099-09-20T10:00:00Z",
  accountRevision: 4,
  changes: [],
  scheduledChanges: [],
  warnings: [],
};
it("uses the effective duration boundary then submits only its server-owned preview id", async () => {
  vi.spyOn(api, "get").mockImplementation(async (path) =>
    path.includes("groups")
      ? {
          items: [
            {
              id: "analysis",
              name: "Analysis",
              projectId: "atlas",
              description: "Analysis work",
              requestable: true,
              resourceIds: ["results"],
            },
          ],
        }
      : ({
          items: [
            {
              id: "a",
              displayName: "Alice",
              status: "Active",
              projectIds: ["atlas"],
              revision: 1,
            },
          ],
          total: 1,
          page: 1,
          pageSize: 25,
        } as never),
  );
  const post = vi
    .spyOn(api, "post")
    .mockResolvedValueOnce(preview)
    .mockResolvedValueOnce({ id: "r1" });
  vi.spyOn(window, "scrollTo").mockImplementation(() => {});
  render(<RequestForm session={{ ...session, maxMembershipDays: 7 }} />);
  await screen.findByRole("option", { name: /Analysis/ });
  expect(screen.getByText("UTC · exclusive · maximum 7 days")).toBeTruthy();
  fireEvent.change(screen.getByLabelText("Group"), {
    target: { value: "analysis" },
  });
  fireEvent.change(screen.getByLabelText("Reason"), {
    target: { value: "Need analysis access" },
  });
  fireEvent.change(screen.getByLabelText("End date"), {
    target: { value: "2026-10-01T12:00" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Preview impact" }));
  await screen.findByRole("alert");
  expect(post).not.toHaveBeenCalled();
  expect(screen.getByText(/within 7 days/)).toBeTruthy();
  fireEvent.change(screen.getByLabelText("End date"), {
    target: { value: "2026-09-25T10:00" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Preview impact" }));
  await screen.findByRole("button", { name: "Submit request" });
  expect(post.mock.calls[0][0]).toBe("/api/previews");
  fireEvent.click(screen.getByRole("button", { name: "Submit request" }));
  await waitFor(() =>
    expect(post.mock.calls[1][1]).toEqual({ previewId: "server-preview" }),
  );
});
it("preserves form arguments when the server rejects a complex preview", async () => {
  vi.spyOn(api, "get").mockImplementation(
    async (p) =>
      (p.includes("groups")
        ? {
            items: [
              {
                id: "analysis",
                name: "Analysis",
                projectId: "atlas",
                requestable: true,
                resourceIds: [],
              },
            ],
          }
        : { items: [], total: 0, page: 1, pageSize: 100 }) as never,
  );
  const post = vi
    .spyOn(api, "post")
    .mockRejectedValue(
      new ApiError(
        400,
        "Too many scheduled boundaries. Shorten the requested period.",
      ),
    );
  render(<RequestForm session={session} />);
  await screen.findByRole("option", { name: /Analysis/ });
  expect(screen.getByText("UTC · exclusive · maximum 90 days")).toBeTruthy();
  fireEvent.change(screen.getByLabelText("Group"), {
    target: { value: "analysis" },
  });
  fireEvent.change(screen.getByLabelText("Reason"), {
    target: { value: "Retain this reason" },
  });
  fireEvent.change(screen.getByLabelText("End date"), {
    target: { value: "2026-10-01T12:00" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Preview impact" }));
  expect((await screen.findByRole("alert")).textContent).toContain(
    "Too many scheduled boundaries",
  );
  expect((screen.getByLabelText("Reason") as HTMLTextAreaElement).value).toBe(
    "Retain this reason",
  );
  expect((screen.getByLabelText("End date") as HTMLInputElement).value).toBe(
    "2026-10-01T12:00",
  );
  expect(screen.queryByRole("button", { name: "Submit request" })).toBeNull();
  expect(post).toHaveBeenCalledTimes(1);
});
it("invalidates a reviewer preview on conflict, preserves original reason, and requires explicit refresh", async () => {
  const request = {
    id: "r1",
    accountId: "a",
    accountName: "Alice",
    actorId: "a",
    actorName: "Alice",
    groupId: "analysis",
    groupName: "Analysis",
    projectId: "atlas",
    operation: "Add",
    startsAt: "2026-09-20T10:00:00Z",
    endsAt: "2026-10-01T10:00:00Z",
    reason: "Research period",
    status: "Pending",
    createdAt: "2026-09-19T10:00:00Z",
    expiresAt: "2026-10-01T10:00:00Z",
    decisionReason: null,
    decidedBy: null,
    correlationId: "c-1",
    canReview: true,
    canCancel: false,
    syncStatus: null,
  };
  vi.spyOn(api, "get").mockResolvedValue(request);
  const post = vi
    .spyOn(api, "post")
    .mockResolvedValueOnce(preview)
    .mockRejectedValueOnce(new ApiError(409, "State changed", "c-2"))
    .mockResolvedValueOnce({ ...preview, id: "p2" });
  render(<RequestReview id="r1" />);
  fireEvent.click(
    await screen.findByRole("button", { name: "Preview impact" }),
  );
  fireEvent.click(await screen.findByRole("button", { name: "Approve" }));
  await screen.findByRole("alert");
  expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
  expect(screen.getByText("Research period")).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Refresh impact" }));
  await screen.findByRole("button", { name: "Approve" });
  expect(post.mock.calls[2][0]).toBe("/api/requests/r1/preview");
});
