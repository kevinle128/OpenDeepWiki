import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { renderWithIntl } from "@/test-utils/render-with-intl";
import { BranchGenerationStatus } from "./branch-generation-status";

describe("BranchGenerationStatus", () => {
  it("renders public branch generation status without mutation controls", () => {
    renderWithIntl(
      <BranchGenerationStatus
        branchId="branch-1"
        branchName="main"
        generationStatus="Failed"
        lastGenerationTaskId="task-1234567890"
        lastGenerationError="generation failed"
      />
    );

    expect(screen.getByText("Branch Generation")).toBeTruthy();
    expect(screen.getByText("Failed")).toBeTruthy();
    expect(screen.getByText("generation failed")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /generate full wiki/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /retry generation/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /cancel/i })).toBeNull();
  });

  it("announces status changes politely and shows localized status text", () => {
    const { container } = renderWithIntl(
      <BranchGenerationStatus branchId="b" branchName="main" generationStatus="Processing" />
    );

    expect(container.querySelector('[aria-live="polite"]')?.textContent).toContain("Processing");
  });
});
