"use client";

import { useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { storeGitHubInstallation } from "@/lib/admin-api";
import { Loader2 } from "lucide-react";

export default function GitHubImportCallbackPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [error, setError] = useState<string | null>(null);
  const installationId = searchParams.get("installation_id");
  const displayedError = installationId ? error : "Missing installation_id parameter";

  useEffect(() => {
    if (!installationId) {
      return;
    }

    const storeAndRedirect = async () => {
      try {
        await storeGitHubInstallation(parseInt(installationId, 10));
        router.push("/admin/github-import");
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to store installation");
      }
    };

    storeAndRedirect();
  }, [installationId, router]);

  if (displayedError) {
    return (
      <div className="flex flex-col items-center justify-center min-h-[400px] gap-4">
        <p className="text-destructive text-lg">Error: {displayedError}</p>
        <button
          onClick={() => router.push("/admin/github-import")}
          className="text-primary underline"
        >
          Back to GitHub Import
        </button>
      </div>
    );
  }

  return (
    <div className="flex flex-col items-center justify-center min-h-[400px] gap-4">
      <Loader2 className="h-8 w-8 animate-spin text-primary" />
      <p className="text-muted-foreground">Connecting GitHub installation...</p>
    </div>
  );
}
