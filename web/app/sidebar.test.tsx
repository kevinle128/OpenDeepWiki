import { describe, expect, it } from "vitest";

import { navGroups } from "./sidebar";

describe("sidebar navigation", () => {
  it("links the repository workspace in the workspace group for signed-in users only", () => {
    const workspace = navGroups.find((group) => group.labelKey === "groupWorkspace");
    const item = workspace?.items.find((entry) => entry.key === "repositories");

    expect(item).toMatchObject({ url: "/repositories", requireAuth: true });
  });
});
