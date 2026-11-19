import { createHash } from "node:crypto";
import { open, readFile, readdir } from "node:fs/promises";
import { join } from "node:path";
import type { CollectorConfig } from "@aivc/config";
import { CollectorError } from "./platform-adapter.ts";

// Caller holds the directory lock. Do not replay an old mock/other-project spool into real API.
export async function bindRealSpool(config: CollectorConfig): Promise<void> {
  const path = join(config.COLLECTOR_SPOOL_DIRECTORY, ".binding");
  const binding = createHash("sha256")
    .update(
      JSON.stringify([
        config.COLLECTOR_MODE,
        config.COLLECTOR_API_URL,
        config.COLLECTOR_TENANT_ID,
        config.COLLECTOR_EXPECTED_PROJECT_ID,
        config.COLLECTOR_NODE_ID,
        // Existing Doubao bindings remain valid, but can never replay as Ernie.
        ...(config.COLLECTOR_SUPPORTED_PLATFORMS[0] === "doubao"
          ? []
          : [config.COLLECTOR_SUPPORTED_PLATFORMS[0]]),
      ]),
    )
    .digest("hex");
  let existing: string | undefined;
  try {
    existing = await readFile(path, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT")
      throw new CollectorError("task_not_approved");
  }
  if (existing !== undefined) {
    if (existing !== binding) throw new CollectorError("task_not_approved");
    return;
  }
  if (
    (await readdir(config.COLLECTOR_SPOOL_DIRECTORY)).some((name) => name !== ".collector.lock")
  ) {
    throw new CollectorError("task_not_approved");
  }
  const file = await open(path, "wx", 0o600);
  try {
    await file.writeFile(binding);
    await file.sync();
  } finally {
    await file.close();
  }
}
