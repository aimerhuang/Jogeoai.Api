import { readFile } from "node:fs/promises";
import { createServer } from "node:net";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import type { CommandRunner, RunningCommand } from "./command.ts";
import type { DriverTools } from "./driver.ts";
import { AppSetupError } from "./failure.ts";
import { realSleep, type Sleep } from "./time.ts";

// The script's own Appium server (design §2): one child process, listening on the loopback
// address only, started through the injectable command runner and stopped when the script ends.
// Its output is discarded, so nothing an App shows can reach this script's logs through it.

// Appium discovers the UiAutomator2 driver from this package's own dependencies.
export const APPIUM_HOME = fileURLToPath(new URL("../../", import.meta.url));
export const APPIUM_BIN = fileURLToPath(new URL("../../node_modules/.bin/appium", import.meta.url));
// Installed on a device by the UiAutomator2 driver (stage 0); listed in batch.json.
export const APPIUM_HELPER_PACKAGES = [
  "io.appium.settings",
  "io.appium.uiautomator2.server",
  "io.appium.uiautomator2.server.test",
];
const TOOL_PACKAGES = ["appium", "appium-uiautomator2-driver", "webdriverio"];
// Passed to the server next to the allowlisted environment: it locates adb and the build tools
// through the SDK and runs its on-device helper build with the JDK.
const SERVER_ENVIRONMENT = ["ANDROID_HOME", "ANDROID_SDK_ROOT", "JAVA_HOME"] as const;
const HOST = "127.0.0.1";
const POLL_MS = 500;

export interface AppiumServerOptions {
  runner: CommandRunner;
  env?: NodeJS.ProcessEnv;
  // Fixed port; default: a free one.
  port?: number;
  // Operator opt-in for debugging: Appium writes its error-level log to this private file. It is
  // Appium's own text (it may quote selectors), so it is never part of this script's logs.
  logFile?: string;
  findPort?: () => Promise<number>;
  // Is the server answering at this /status URL?
  probe?: (url: string) => Promise<boolean>;
  sleep?: Sleep;
  startTimeoutMs?: number;
  stopTimeoutMs?: number;
}

export function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once("error", reject);
    server.listen(0, HOST, () => {
      const address = server.address();
      server.close(() =>
        typeof address === "object" && address ? resolve(address.port) : reject(new Error()),
      );
    });
  });
}
async function statusReady(url: string): Promise<boolean> {
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(2_000) });
    if (!response.ok) return false;
    const body = (await response.json()) as { value?: { ready?: boolean } };
    return body.value?.ready !== false;
  } catch {
    return false;
  }
}

export class AppiumServer {
  private constructor(
    readonly port: number,
    private readonly child: RunningCommand,
    private readonly sleep: Sleep,
    private readonly stopTimeoutMs: number,
  ) {}

  static async start(options: AppiumServerOptions): Promise<AppiumServer> {
    const env = options.env ?? process.env;
    const sleep = options.sleep ?? realSleep;
    const passed = Object.fromEntries(
      SERVER_ENVIRONMENT.flatMap((key) => (env[key] ? [[key, env[key]] as const] : [])),
    );
    if (!passed.ANDROID_HOME && !passed.ANDROID_SDK_ROOT)
      throw new AppSetupError("appium_unavailable", [
        { path: "ANDROID_HOME", message: "must point at the Android SDK to start Appium" },
      ]);
    let port: number;
    try {
      port = options.port ?? (await (options.findPort ?? freePort)());
    } catch {
      throw new AppSetupError("appium_unavailable");
    }
    const child = options.runner.start(
      APPIUM_BIN,
      [
        "server",
        "--address",
        HOST,
        "--port",
        String(port),
        "--log-level",
        "error",
        ...(options.logFile ? ["--log", options.logFile] : []),
      ],
      { env: { ...passed, APPIUM_HOME } },
    );
    let exited = false;
    void child.exited.then(() => {
      exited = true;
    });
    const server = new AppiumServer(port, child, sleep, options.stopTimeoutMs ?? 10_000);
    const probe = options.probe ?? statusReady;
    const url = `http://${HOST}:${port}/status`;
    for (let waited = 0; waited <= (options.startTimeoutMs ?? 60_000); waited += POLL_MS) {
      if (exited) break;
      if (await probe(url)) return server;
      await sleep(POLL_MS);
    }
    await server.stop();
    throw new AppSetupError("appium_unavailable");
  }

  // SIGTERM lets Appium end its sessions; a server that ignores it is killed.
  async stop(): Promise<void> {
    this.child.kill("SIGTERM");
    const stopped = await Promise.race([
      this.child.exited.then(() => true),
      this.sleep(this.stopTimeoutMs).then(() => false),
    ]);
    if (!stopped) {
      this.child.kill("SIGKILL");
      await this.child.exited;
    }
  }
}

// Versions of the installed automation tools, read from their package manifests.
export async function readDriverTools(home: string = APPIUM_HOME): Promise<DriverTools> {
  const tools: Record<string, string> = {};
  for (const name of TOOL_PACKAGES) {
    try {
      const manifest = JSON.parse(
        await readFile(join(home, "node_modules", name, "package.json"), "utf8"),
      ) as { version?: unknown };
      tools[name] = typeof manifest.version === "string" ? manifest.version : "unknown";
    } catch {
      tools[name] = "unknown";
    }
  }
  return { tools, helperPackages: [...APPIUM_HELPER_PACKAGES] };
}
