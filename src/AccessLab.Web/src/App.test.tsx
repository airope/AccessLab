import { it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { App } from "./App";
import { api } from "./api";
it("routes real URLs through an accessible workspace without privileged navigation for members", async () => {
  history.replaceState({}, "", "/groups");
  vi.spyOn(window, "scrollTo").mockImplementation(() => {});
  vi.spyOn(api, "get").mockImplementation(async (path) =>
    path === "/api/session"
      ? {
          accountId: "a",
          displayName: "Alice",
          status: "Active",
          roles: [{ role: "Member", projectId: "atlas" }],
          projectIds: ["atlas"],
          demoMode: true,
        }
      : ({ items: [], total: 0, page: 1, pageSize: 25 } as never),
  );
  render(<App />);
  await screen.findByRole("heading", { name: "Groups", level: 1 });
  expect(screen.getByRole("navigation", { name: "Workspace" })).toBeTruthy();
  expect(screen.queryByRole("link", { name: "Operations" })).toBeNull();
  expect(screen.queryByLabelText(/role/i)).toBeNull();
  fireEvent.click(screen.getByRole("link", { name: "Audit" }));
  await screen.findByRole("heading", { name: "Audit", level: 1 });
  expect(location.pathname).toBe("/audit");
});
