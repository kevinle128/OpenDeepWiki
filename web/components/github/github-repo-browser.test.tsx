import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { toast } from "sonner";
import { renderWithIntl } from "@/test-utils/render-with-intl";
import { GitHubRepoBrowser, type BatchImportResult, type GitHubInstallation, type GitHubRepo, type GitHubRepoList } from "./github-repo-browser";

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

function pendingResponse() {
  let resolve!: (value: GitHubRepoList) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<GitHubRepoList>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}

function installation(id: number): GitHubInstallation {
  return { id: String(id), installationId: id, accountLogin: `owner-${id}`, accountType: "Organization", accountId: id, createdAt: "2026-10-04" };
}

function repository(id: number): GitHubRepo {
  return { id, name: `repo-${id}`, fullName: `owner-${id}/repo-${id}`, owner: `owner-${id}`, private: false, stargazersCount: 0, forksCount: 0, defaultBranch: "main", cloneUrl: `https://github.com/owner-${id}/repo-${id}.git`, htmlUrl: `https://github.com/owner-${id}/repo-${id}`, alreadyImported: false };
}

it("ignores a previous installation's pending page and imports only the current installation's repos", async () => {
  const previousPage = pendingResponse();
  const currentPage = pendingResponse();
  const fetchRepos = vi.fn((id: number, page: number): Promise<GitHubRepoList> => {
    if (id === 1 && page === 1) {
      return Promise.resolve({ totalCount: 201, repositories: [repository(1)], page: 1, perPage: 100 });
    }
    if (id === 1 && page === 2) return previousPage.promise;
    if (id === 2) return currentPage.promise;
    throw new Error("An obsolete request must not fetch another page");
  });
  const onImport = vi.fn().mockResolvedValue({ totalRequested: 1, imported: 1, skipped: 0, skippedRepos: [], importedRepos: ["owner-2/repo-2"] });
  const props = { fetchRepos, onImport, departments: [{ id: "department", name: "Department" }] };
  const { rerender, container } = renderWithIntl(<GitHubRepoBrowser {...props} installation={installation(1)} />);
  await waitFor(() => expect(fetchRepos).toHaveBeenCalledWith(1, 2, 100));

  rerender(<GitHubRepoBrowser {...props} installation={installation(2)} />);
  await waitFor(() => expect(fetchRepos).toHaveBeenCalledWith(2, 1, 100));
  await act(async () => {
    previousPage.resolve({ totalCount: 201, repositories: [repository(3)], page: 2, perPage: 100 });
  });
  expect(screen.queryByText("owner-1/repo-1")).toBeNull();
  expect(screen.queryByText("owner-3/repo-3")).toBeNull();
  expect(fetchRepos).not.toHaveBeenCalledWith(1, 3, 100);
  expect(container.querySelector(".animate-spin")).not.toBeNull();

  await act(async () => {
    currentPage.resolve({ totalCount: 1, repositories: [repository(2)], page: 1, perPage: 100 });
  });
  const row = screen.getByText("owner-2/repo-2").closest("div.flex.items-center.gap-3")!;
  fireEvent.click(within(row as HTMLElement).getByRole("checkbox"));
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Import 1 Repositories" }));
  });
  expect(onImport).toHaveBeenCalledWith(expect.objectContaining({ installationId: 2, languageCode: "en", repos: [expect.objectContaining({ fullName: "owner-2/repo-2" })] }));
});

it("ignores an obsolete installation's error while the current request is loading", async () => {
  const previousPage = pendingResponse();
  const currentPage = pendingResponse();
  const fetchRepos = vi.fn((id: number) => id === 1 ? previousPage.promise : currentPage.promise);
  const toastError = vi.spyOn(toast, "error");
  const props = { fetchRepos, onImport: vi.fn(), departments: [] };
  const { rerender, container } = renderWithIntl(<GitHubRepoBrowser {...props} installation={installation(1)} />);
  rerender(<GitHubRepoBrowser {...props} installation={installation(2)} />);
  await act(async () => { previousPage.reject(new Error("Previous installation failed")); });
  expect(toastError).not.toHaveBeenCalled();
  expect(container.querySelector(".animate-spin")).not.toBeNull();
});

it("ignores a pending import result after switching installations and returning to the original installation", async () => {
  let finishImport!: (value: BatchImportResult) => void;
  const importResponse = new Promise<BatchImportResult>((resolve) => { finishImport = resolve; });
  const onImport = vi.fn(() => importResponse);
  const fetchRepos = vi.fn((id: number) => Promise.resolve({ totalCount: 1, repositories: [repository(id)], page: 1, perPage: 100 }));
  const toastSuccess = vi.spyOn(toast, "success");
  const props = { fetchRepos, onImport, departments: [{ id: "department", name: "Department" }] };
  const { rerender, container } = renderWithIntl(<GitHubRepoBrowser {...props} installation={installation(1)} />);
  const selectRepo = (id: number) => {
    const row = screen.getByText(`owner-${id}/repo-${id}`).closest("div.flex.items-center.gap-3")!;
    fireEvent.click(within(row as HTMLElement).getByRole("checkbox"));
  };
  await screen.findByText("owner-1/repo-1");
  selectRepo(1);
  fireEvent.click(screen.getByRole("button", { name: "Import 1 Repositories" }));
  expect(onImport).toHaveBeenCalledTimes(1);

  rerender(<GitHubRepoBrowser {...props} installation={installation(2)} />);
  await screen.findByText("owner-2/repo-2");
  selectRepo(2);
  expect(screen.getByRole("button", { name: "Import 1 Repositories" })).toBeEnabled();
  rerender(<GitHubRepoBrowser {...props} installation={installation(1)} />);
  await screen.findByText("owner-1/repo-1");
  await act(async () => {
    finishImport({ totalRequested: 1, imported: 1, skipped: 0, skippedRepos: [], importedRepos: ["owner-1/repo-1"] });
  });
  expect(toastSuccess).not.toHaveBeenCalled();
  expect(fetchRepos).toHaveBeenCalledTimes(3);
  expect(container.querySelector(".animate-spin")).toBeNull();
  selectRepo(1);
  expect(screen.getByRole("button", { name: "Import 1 Repositories" })).toBeEnabled();
});
