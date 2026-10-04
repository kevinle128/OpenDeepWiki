"use client";

import { useState, useEffect } from "react";
import { useTranslations } from "@/hooks/use-translations";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Loader2 } from "lucide-react";
import {
  getAppStatistics,
  AggregatedStatisticsDto,
  formatDateForApi,
} from "@/lib/apps-api";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
  BarChart,
  Bar,
} from "recharts";

interface AppStatisticsChartProps {
  appId: string;
}

type DateRange = "7" | "30" | "90";

export function AppStatisticsChart({ appId }: AppStatisticsChartProps) {
  const t = useTranslations();
  const [statistics, setStatistics] = useState<AggregatedStatisticsDto | null>(
    null
  );
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dateRange, setDateRange] = useState<DateRange>("30");
  const requestKey = `${appId}:${dateRange}`;
  const [lastRequestKey, setLastRequestKey] = useState(requestKey);
  if (requestKey !== lastRequestKey) {
    setLastRequestKey(requestKey);
    setIsLoading(true);
    setError(null);
  }

  useEffect(() => {
    let active = true;
    const endDate = new Date();
    const startDate = new Date();
    startDate.setDate(startDate.getDate() - parseInt(dateRange));

    getAppStatistics(
      appId,
      formatDateForApi(startDate),
      formatDateForApi(endDate)
    ).then((data) => {
      if (active) setStatistics(data);
    }).catch((err: unknown) => {
      if (active) setError(err instanceof Error ? err.message : "Failed to load statistics");
    }).finally(() => {
      if (active) setIsLoading(false);
    });
    return () => { active = false; };
  }, [appId, dateRange]);

  const formatNumber = (num: number) => {
    if (num >= 1000000) return `${(num / 1000000).toFixed(1)}M`;
    if (num >= 1000) return `${(num / 1000).toFixed(1)}K`;
    return num.toString();
  };

  const formatDate = (dateStr: string) => {
    const date = new Date(dateStr);
    return `${date.getMonth() + 1}/${date.getDate()}`;
  };

  if (isLoading) {
    return (
      <Card>
        <CardContent className="flex items-center justify-center py-12">
          <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
        </CardContent>
      </Card>
    );
  }

  if (error) {
    return (
      <Card>
        <CardContent className="py-12">
          <div className="text-center text-destructive">{error}</div>
        </CardContent>
      </Card>
    );
  }

  if (!statistics || statistics.dailyStatistics.length === 0) {
    return (
      <Card>
        <CardContent className="py-12">
          <div className="text-center text-muted-foreground">
            {t("apps.statistics.noData")}
          </div>
        </CardContent>
      </Card>
    );
  }

  const chartData = statistics.dailyStatistics.map((stat) => ({
    date: formatDate(stat.date),
    requests: stat.requestCount,
    inputTokens: stat.inputTokens,
    outputTokens: stat.outputTokens,
  }));

  return (
    <div className="space-y-4">
      {/* Summary Cards */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              {t("apps.statistics.totalRequests")}
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">
              {formatNumber(statistics.totalRequests)}
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              {t("apps.statistics.totalInputTokens")}
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">
              {formatNumber(statistics.totalInputTokens)}
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              {t("apps.statistics.totalOutputTokens")}
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div className="text-2xl font-bold">
              {formatNumber(statistics.totalOutputTokens)}
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Date Range Selector */}
      <div className="flex items-center gap-2">
        <span className="text-sm text-muted-foreground">
          {t("apps.statistics.dateRange")}:
        </span>
        <div className="flex gap-1">
          <Button
            variant={dateRange === "7" ? "default" : "outline"}
            size="sm"
            onClick={() => setDateRange("7")}
          >
            {t("apps.statistics.last7Days")}
          </Button>
          <Button
            variant={dateRange === "30" ? "default" : "outline"}
            size="sm"
            onClick={() => setDateRange("30")}
          >
            {t("apps.statistics.last30Days")}
          </Button>
          <Button
            variant={dateRange === "90" ? "default" : "outline"}
            size="sm"
            onClick={() => setDateRange("90")}
          >
            {t("apps.statistics.last90Days")}
          </Button>
        </div>
      </div>

      {/* Charts */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {/* Daily Requests Chart */}
        <Card>
          <CardHeader>
            <CardTitle className="text-base">
              {t("apps.statistics.dailyRequests")}
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div className="h-[300px]">
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={chartData}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis dataKey="date" fontSize={12} />
                  <YAxis fontSize={12} />
                  <Tooltip />
                  <Bar
                    dataKey="requests"
                    fill="hsl(var(--primary))"
                    name={t("apps.statistics.totalRequests")}
                  />
                </BarChart>
              </ResponsiveContainer>
            </div>
          </CardContent>
        </Card>

        {/* Daily Tokens Chart */}
        <Card>
          <CardHeader>
            <CardTitle className="text-base">
              {t("apps.statistics.dailyTokens")}
            </CardTitle>
          </CardHeader>
          <CardContent>
            <div className="h-[300px]">
              <ResponsiveContainer width="100%" height="100%">
                <LineChart data={chartData}>
                  <CartesianGrid strokeDasharray="3 3" />
                  <XAxis dataKey="date" fontSize={12} />
                  <YAxis fontSize={12} />
                  <Tooltip />
                  <Legend />
                  <Line
                    type="monotone"
                    dataKey="inputTokens"
                    stroke="hsl(var(--primary))"
                    name={t("apps.statistics.totalInputTokens")}
                    strokeWidth={2}
                  />
                  <Line
                    type="monotone"
                    dataKey="outputTokens"
                    stroke="hsl(142, 76%, 36%)"
                    name={t("apps.statistics.totalOutputTokens")}
                    strokeWidth={2}
                  />
                </LineChart>
              </ResponsiveContainer>
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
