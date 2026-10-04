import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

/** Every regular file below a directory. A missing directory has no files. */
export function walkFiles(directory: string): string[] {
  if (!fs.existsSync(directory)) return [];
  const files: string[] = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walkFiles(full));
    else if (entry.isFile()) files.push(full);
  }
  return files;
}

/**
 * True when the file holds the text. A zip (a Playwright trace) is opened and every entry is searched, because
 * its entries are compressed and a search of the raw bytes would miss them.
 */
export function fileContains(file: string, needle: string): boolean {
  const raw = fs.readFileSync(file);
  if (raw.includes(needle)) return true;
  if (raw.subarray(0, 2).toString("latin1") !== "PK") return false;
  const unpacked = execFileSync("unzip", ["-p", file], { maxBuffer: 1024 * 1024 * 1024 });
  return unpacked.includes(needle);
}

/** The files below the directories that hold the text. Content is never returned, only paths. */
export function filesContaining(directories: string[], needle: string): string[] {
  return directories.flatMap((directory) => walkFiles(directory)).filter((file) => fileContains(file, needle));
}
