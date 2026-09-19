import { it, expect, vi } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { GroupsPage, AccountsPage } from "./directory";
import { api, ApiError } from "./api";
const session = {
  accountId: "a",
  displayName: "Alice",
  status: "Active",
  roles: [{ role: "Member", projectId: "atlas" }],
  projectIds: ["atlas"],
  demoMode: true,
};
it("pages scoped group members and links to this group request history", async () => {
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  const get = vi.spyOn(api, "get").mockImplementation(
    async (path) =>
      (path.includes("/members")
        ? { items: [], total: 26, page: 1, pageSize: 25 }
        : {
            items: [
              {
                id: "analysis",
                name: "Analysis",
                projectId: "atlas",
                description: "Research",
                requestable: true,
                resourceIds: [],
              },
            ],
          }) as never,
  );
  render(
    <GroupsPage
      session={{
        ...session,
        roles: [{ role: "Reviewer", projectId: "atlas" }],
      }}
    />,
  );
  fireEvent.click(await screen.findByRole("button", { name: /View members/ }));
  fireEvent.click(await screen.findByRole("button", { name: "Next" }));
  await waitFor(() =>
    expect(
      get.mock.calls.some(
        ([p]) => p === "/api/groups/analysis/members?page=2&pageSize=25",
      ),
    ).toBe(true),
  );
  expect(
    screen.getByRole("link", { name: /Browse requests/ }).getAttribute("href"),
  ).toBe("/requests?groupId=analysis");
});
it("does not fetch or expose group members to a member-only account", async () => {
  const get = vi.spyOn(api, "get").mockResolvedValue({
    items: [
      {
        id: "analysis",
        name: "Analysis",
        description: "Research",
        projectId: "atlas",
        requestable: true,
        resourceIds: ["results"],
      },
    ],
  });
  render(<GroupsPage session={session} />);
  await screen.findByText("Analysis");
  expect(screen.queryByRole("button", { name: /View members/ })).toBeNull();
  expect(get.mock.calls.every(([path]) => !path.includes("/members"))).toBe(
    true,
  );
});
it("requires a reason for an operator lifecycle change and retains it after service failure", async () => {
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  vi.spyOn(api, "get").mockResolvedValue({
    items: [
      {
        id: "a",
        displayName: "Alice",
        status: "Active",
        projectIds: ["atlas"],
        revision: 2,
        startsAt: "2026-01-01T00:00:00Z",
        endsAt: null,
      },
    ],
    total: 1,
    page: 1,
    pageSize: 25,
  });
  const post = vi
    .spyOn(api, "post")
    .mockRejectedValueOnce(new ApiError(503, "Database unavailable"));
  render(
    <AccountsPage
      session={{ ...session, roles: [{ role: "Operator", projectId: null }] }}
    />,
  );
  await screen.findByText("No end limit");
  expect(screen.getByText("1 Jan 2026, 00:00 UTC")).toBeTruthy();
  fireEvent.click(await screen.findByRole("button", { name: "Manage Alice" }));
  fireEvent.change(screen.getByLabelText("New status"), {
    target: { value: "Suspended" },
  });
  fireEvent.change(screen.getByLabelText("Reason"), {
    target: { value: "Temporary departure" },
  });
  fireEvent.click(
    screen.getByRole("button", { name: "Confirm status change" }),
  );
  await screen.findByRole("alert");
  expect((screen.getByLabelText("Reason") as HTMLTextAreaElement).value).toBe(
    "Temporary departure",
  );
  await waitFor(() =>
    expect(post.mock.calls[0][1]).toEqual({
      status: "Suspended",
      reason: "Temporary departure",
    }),
  );
});
