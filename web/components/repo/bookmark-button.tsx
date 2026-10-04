"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Bookmark, Loader2 } from "lucide-react";
import { useAuth } from "@/contexts/auth-context";
import { addBookmark, removeBookmark, getBookmarkStatus } from "@/lib/bookmark-api";

interface BookmarkButtonProps {
  repositoryId: string;
  bookmarkCount?: number;
  onCountChange?: (delta: number) => void;
}

export function BookmarkButton({ repositoryId, bookmarkCount = 0, onCountChange }: BookmarkButtonProps) {
  const { user, isAuthenticated } = useAuth();
  const router = useRouter();
  const [isBookmarked, setIsBookmarked] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [count, setCount] = useState(bookmarkCount);
  const [previousCount, setPreviousCount] = useState(bookmarkCount);
  if (previousCount !== bookmarkCount) {
    setPreviousCount(bookmarkCount);
    setCount(bookmarkCount);
  }

  useEffect(() => {
    if (isAuthenticated && user?.id) {
      getBookmarkStatus(repositoryId, user.id)
        .then(status => setIsBookmarked(status.isBookmarked))
        .catch(() => setIsBookmarked(false));
    }
  }, [repositoryId, user?.id, isAuthenticated]);

  const handleClick = async () => {
    if (!isAuthenticated || !user?.id) {
      // Redirect to login or show login prompt
      router.push("/auth");
      return;
    }

    setIsLoading(true);
    try {
      if (isBookmarked) {
        const response = await removeBookmark(repositoryId, user.id);
        if (response.success) {
          setIsBookmarked(false);
          setCount(prev => Math.max(0, prev - 1));
          onCountChange?.(-1);
        }
      } else {
        const response = await addBookmark({ userId: user.id, repositoryId });
        if (response.success) {
          setIsBookmarked(true);
          setCount(prev => prev + 1);
          onCountChange?.(1);
        }
      }
    } catch (error) {
      console.error("Bookmark operation failed:", error);
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <Button
      variant={isBookmarked ? "default" : "outline"}
      size="sm"
      onClick={handleClick}
      disabled={isLoading}
      className="gap-1.5"
    >
      {isLoading ? (
        <Loader2 className="h-4 w-4 animate-spin" />
      ) : (
        <Bookmark className={`h-4 w-4 ${isBookmarked ? "fill-current" : ""}`} />
      )}
      <span>{count}</span>
    </Button>
  );
}
