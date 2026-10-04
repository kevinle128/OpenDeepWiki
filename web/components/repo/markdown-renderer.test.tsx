import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { MarkdownRenderer } from "./markdown-renderer";

vi.mock("next-themes", () => ({ useTheme: () => ({ resolvedTheme: "dark" }) }));

describe("MarkdownRenderer", () => {
  it("keeps duplicate heading anchors stable when rendering the same content again", () => {
    const content = "## Overview\n\nFirst section.\n\n## Overview\n\nSecond section.";
    const { rerender } = render(<MarkdownRenderer content={content} language="en" />);
    expect(screen.getAllByRole("heading").map((heading) => heading.id)).toEqual(["overview", "overview-1"]);

    rerender(<MarkdownRenderer content={content} language="zh" />);
    expect(screen.getAllByRole("heading").map((heading) => heading.id)).toEqual(["overview", "overview-1"]);
  });
});
