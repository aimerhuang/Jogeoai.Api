import { setTimeout as delay } from "node:timers/promises";
import { loadCollectorConfig, loadSessionConfig } from "@aivc/config";
import { createRealAdapter } from "./adapters/real-platform.ts";
import { type ApprovedQuestions, loadApprovedQuestions } from "./approval-guard.ts";
import { ControlPlaneClient } from "./control-plane-client.ts";
import { createNotifier } from "./notify.ts";
import { AdapterRegistry, mockAdapter, safeFailure } from "./platform-adapter.ts";
import { exitCodeFor, runCollector } from "./runner.ts";
import { lockProfile, openSession } from "./session-profile.ts";
import { bindRealSpool } from "./spool-binding.ts";
import type { Page } from "playwright";

async function main(): Promise<void> {
  const config = loadCollectorConfig();
  const controller = new AbortController();
  const stop = () => controller.abort();
  process.once("SIGINT", stop);
  process.once("SIGTERM", stop);
  const log = (metadata: object) =>
    console.log(
      JSON.stringify({
        level: "info",
        service: "collector",
        nodeId: config.COLLECTOR_NODE_ID,
        mode: config.COLLECTOR_MODE,
        ...metadata,
      }),
    );
  let session: Awaited<ReturnType<typeof openSession>> | undefined;
  let collectionPage: Page | undefined;
  let unlockSpool: (() => Promise<void>) | undefined;
  try {
    let registry: AdapterRegistry;
    let profileDirectory: string | undefined;
    let approvedQuestions: ApprovedQuestions | undefined;
    if (config.COLLECTOR_MODE === "real") {
      // A bad approved-question file exits here: no spool lock, browser or lease is touched.
      approvedQuestions = await loadApprovedQuestions(config);
      const browserConfig = loadSessionConfig();
      unlockSpool = await lockProfile(config.COLLECTOR_SPOOL_DIRECTORY);
      await bindRealSpool(config);
      profileDirectory = browserConfig.COLLECTOR_PROFILE_DIRECTORY;
      session = await openSession(
        profileDirectory,
        browserConfig.COLLECTOR_BROWSER_HEADLESS === "true",
        browserConfig.COLLECTOR_BROWSER_CDP_URL,
      );
      // Never use the user's currently visible CDP tab. A dedicated tab shares
      // its authenticated browser context, while abort/cleanup can close only
      // Collector-owned UI state.
      collectionPage = await session.context.newPage();
      registry = new AdapterRegistry([await createRealAdapter(collectionPage, browserConfig)]);
    } else registry = new AdapterRegistry(config.COLLECTOR_SUPPORTED_PLATFORMS.map(mockAdapter));
    const client = new ControlPlaneClient(config);
    const notify = createNotifier({ log, command: config.COLLECTOR_NOTIFY_COMMAND });
    do {
      try {
        const processed = await runCollector(
          config,
          client,
          registry,
          controller.signal,
          log,
          profileDirectory,
          approvedQuestions,
          { notify },
        );
        log({ event: "collector_finished", processed });
        break;
      } catch (error) {
        const code = safeFailure(error);
        log({ event: "collector_stopped", code });
        if (controller.signal.aborted) break;
        if (config.COLLECTOR_MODE === "real" || config.COLLECTOR_RUN_ONCE === "true") {
          process.exitCode = exitCodeFor(code);
          break;
        }
        await delay(config.COLLECTOR_POLL_INTERVAL_MS, undefined, { signal: controller.signal });
      }
    } while (!controller.signal.aborted);
  } finally {
    try {
      await collectionPage?.close().catch(() => undefined);
      await session?.close();
    } finally {
      await unlockSpool?.();
    }
    process.removeListener("SIGINT", stop);
    process.removeListener("SIGTERM", stop);
  }
}
await main().catch((error) => {
  console.error(JSON.stringify({ event: "collector_failed", code: safeFailure(error) }));
  process.exitCode = 1;
});
