import fs from "node:fs";

import { AUTH_DIR } from "./env";

/** The session files hold test tokens only. They are removed when the run ends. */
export default async function globalTeardown(): Promise<void> {
  fs.rmSync(AUTH_DIR, { recursive: true, force: true });
}
