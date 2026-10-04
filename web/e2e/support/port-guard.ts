import { execFileSync } from "node:child_process";

/**
 * Describes the owner of a listening TCP port, or null when the port is free. Uses lsof, so it covers macOS
 * and Linux. Where lsof is missing, the check is skipped and Playwright's own busy-port error still applies.
 */
function describePortOwner(port: number): string | null {
  try {
    const output = execFileSync("lsof", ["-nP", `-iTCP:${port}`, "-sTCP:LISTEN", "-Fpc"], { encoding: "utf8" });
    const pid = /^p(\d+)$/m.exec(output)?.[1];
    const command = /^c(.+)$/m.exec(output)?.[1];
    return pid ? `PID ${pid} (${command ?? "unknown"})` : null;
  } catch {
    // lsof exits with 1 when nothing matches, and it may be missing. Both mean there is no owner to name.
    return null;
  }
}

/**
 * Stops the run when a fixed port is taken. The error names the owner so that a stale project process can be
 * stopped on purpose. The runner never reuses a server and never moves to another port.
 */
export function assertPortsFree(ports: number[]): void {
  const taken = ports
    .map((port) => ({ port, owner: describePortOwner(port) }))
    .filter((entry): entry is { port: number; owner: string } => entry.owner !== null);
  if (taken.length === 0) return;
  const lines = taken.map(({ port, owner }) => `  port ${port} is used by ${owner}`);
  throw new Error(
    `The end-to-end ports are not free:\n${lines.join("\n")}\nStop the stale process (kill <pid>) and run again. ` +
      "Other services on this machine are never touched by the runner.",
  );
}
