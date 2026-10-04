"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Bell, BellOff, Loader2 } from "lucide-react";
import { useAuth } from "@/contexts/auth-context";
import { addSubscription, removeSubscription, getSubscriptionStatus } from "@/lib/subscription-api";

interface SubscribeButtonProps {
  repositoryId: string;
  subscriptionCount?: number;
  onCountChange?: (delta: number) => void;
}

export function SubscribeButton({ repositoryId, subscriptionCount = 0, onCountChange }: SubscribeButtonProps) {
  const { user, isAuthenticated } = useAuth();
  const router = useRouter();
  const [isSubscribed, setIsSubscribed] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [count, setCount] = useState(subscriptionCount);
  const [previousCount, setPreviousCount] = useState(subscriptionCount);
  if (previousCount !== subscriptionCount) {
    setPreviousCount(subscriptionCount);
    setCount(subscriptionCount);
  }

  useEffect(() => {
    if (isAuthenticated && user?.id) {
      getSubscriptionStatus(repositoryId, user.id)
        .then(status => setIsSubscribed(status.isSubscribed))
        .catch(() => setIsSubscribed(false));
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
      if (isSubscribed) {
        const response = await removeSubscription(repositoryId, user.id);
        if (response.success) {
          setIsSubscribed(false);
          setCount(prev => Math.max(0, prev - 1));
          onCountChange?.(-1);
        }
      } else {
        const response = await addSubscription({ userId: user.id, repositoryId });
        if (response.success) {
          setIsSubscribed(true);
          setCount(prev => prev + 1);
          onCountChange?.(1);
        }
      }
    } catch (error) {
      console.error("Subscription operation failed:", error);
    } finally {
      setIsLoading(false);
    }
  };

  return (
    <Button
      variant={isSubscribed ? "default" : "outline"}
      size="sm"
      onClick={handleClick}
      disabled={isLoading}
      className="gap-1.5"
    >
      {isLoading ? (
        <Loader2 className="h-4 w-4 animate-spin" />
      ) : isSubscribed ? (
        <Bell className="h-4 w-4 fill-current" />
      ) : (
        <BellOff className="h-4 w-4" />
      )}
      <span>{count}</span>
    </Button>
  );
}
