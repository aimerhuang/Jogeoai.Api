import { loadSessionConfig, requireSessionReporting } from "@aivc/config";
import { createRealAdapter, realPlatformUrl } from "./adapters/real-platform.ts";
import { postSessionEvent } from "./control-plane-client.ts";
import { safeFailure } from "./platform-adapter.ts";
import { openSession } from "./session-profile.ts";
import type { Page } from "playwright";

async function main(): Promise<void> {
  const command = process.argv[2];
  if (command !== "login" && command !== "probe") throw new Error("invalid_command");
  const config = loadSessionConfig();
  // With a registered account, a ready probe is the operator's recovery signal. Validate the
  // reporting settings before any browser is opened.
  const reporting = command === "probe" ? requireSessionReporting(config) : undefined;
  const session = await openSession(
    config.COLLECTOR_PROFILE_DIRECTORY,
    command === "probe" && config.COLLECTOR_BROWSER_HEADLESS === "true",
    config.COLLECTOR_BROWSER_CDP_URL,
  );
  const controller = new AbortController();
  let page: Page | undefined;
  const stop = () => controller.abort();
  process.once("SIGINT", stop);
  process.once("SIGTERM", stop);
  try {
    // Do not navigate an operator's existing CDP tab while probing or opening
    // the manual-login flow. This tab shares the selected Chrome profile only.
    page = await session.context.newPage();
    if (command === "login") {
      await page.goto(realPlatformUrl(config), { waitUntil: "domcontentloaded", timeout: 30_000 });
      console.log(
        JSON.stringify({
          event: "manual_login_open",
          platformCode: config.COLLECTOR_SUPPORTED_PLATFORMS,
          instruction: "Complete login manually. Close the browser or press Ctrl+C when finished.",
        }),
      );
      await new Promise<void>((resolve) => {
        session.context.once("close", () => resolve());
        controller.signal.addEventListener("abort", () => resolve(), { once: true });
        if (controller.signal.aborted) resolve();
      });
    } else {
      const adapter = await createRealAdapter(page, config);
      const result = await adapter.probe(
        AbortSignal.any([controller.signal, AbortSignal.timeout(45_000)]),
      );
      console.log(
        JSON.stringify({ event: `${adapter.platformCode}_probe`, status: result.status }),
      );
      if (result.status !== "ready") process.exitCode = 2;
      else if (reporting) {
        try {
          const response = await postSessionEvent(
            reporting,
            {
              tenantId: reporting.COLLECTOR_TENANT_ID,
              nodeId: reporting.COLLECTOR_NODE_ID,
              region: reporting.COLLECTOR_REGION,
              sessionProfileId: reporting.COLLECTOR_SESSION_PROFILE_ID,
              eventKind: "verified",
              phase: "probe",
            },
            controller.signal,
          );
          console.log(
            JSON.stringify({
              event: "session_verified",
              sessionProfileId: reporting.COLLECTOR_SESSION_PROFILE_ID,
              status: response.sessionProfileStatus,
            }),
          );
        } catch (error) {
          // The page is ready but the account was not restored; the operator must retry.
          console.error(
            JSON.stringify({ event: "session_verified_report_failed", code: safeFailure(error) }),
          );
          process.exitCode = 1;
        }
      }
    }
  } finally {
    await page?.close().catch(() => undefined);
    await session.close();
    process.removeListener("SIGINT", stop);
    process.removeListener("SIGTERM", stop);
  }
}
await main().catch((error) => {
  console.error(JSON.stringify({ event: "session_failed", code: safeFailure(error) }));
  process.exitCode = 1;
});
