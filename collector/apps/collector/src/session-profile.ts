import { access, lstat, mkdir, open, realpath, unlink } from "node:fs/promises";
import { dirname, isAbsolute, join, parse, resolve } from "node:path";
import { homedir } from "node:os";
import { chromium, type BrowserContext } from "playwright";
import { CollectorError } from "./platform-adapter.ts";

// Only dedicated aivc directories; never point Chromium at a personal browser profile.
export async function secureDirectory(directory: string): Promise<string> {
  const target = resolve(directory);
  if (
    !isAbsolute(directory) ||
    target === homedir() ||
    target === parse(target).root ||
    !target.includes("/aivc/") ||
    /\/(Google|Chrome|Chromium|Microsoft Edge|Firefox)\//i.test(target)
  ) {
    throw new CollectorError("profile_unavailable");
  }
  let ancestor = target;
  while (true) {
    try {
      if ((await lstat(ancestor)).isSymbolicLink()) throw new CollectorError("profile_unavailable");
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    try {
      await access(join(ancestor, ".git"));
      throw new CollectorError("profile_unavailable");
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    if (ancestor === dirname(ancestor)) break;
    ancestor = dirname(ancestor);
  }
  await mkdir(target, { recursive: true, mode: 0o700 });
  const stat = await lstat(target);
  if (!stat.isDirectory() || (stat.mode & 0o077) !== 0 || stat.uid !== process.getuid?.()) {
    throw new CollectorError("profile_unavailable");
  }
  return realpath(target);
}

export async function lockProfile(directory: string): Promise<() => Promise<void>> {
  const target = await secureDirectory(directory);
  const lock = join(target, ".collector.lock");
  try {
    const handle = await open(lock, "wx", 0o600);
    await handle.writeFile(JSON.stringify({ pid: process.pid }));
    await handle.sync();
    await handle.close();
  } catch {
    throw new CollectorError("profile_unavailable");
  }
  return async () => {
    await unlink(lock);
  };
}

// Child processes (Chromium, operator notification command) never inherit tokens, keys,
// proxies or debug settings: only these basic OS variables are passed through.
export function allowlistedEnvironment(
  env: NodeJS.ProcessEnv = process.env,
): Record<string, string> {
  return Object.fromEntries(
    Object.entries(env).filter(
      ([key, value]) =>
        value !== undefined &&
        /^(PATH|HOME|TMPDIR|TEMP|TMP|DISPLAY|XAUTHORITY|LANG|LC_ALL|SYSTEMROOT|WINDIR|USERPROFILE|LOCALAPPDATA)$/.test(
          key,
        ),
    ),
  ) as Record<string, string>;
}

export async function openSession(
  directory: string,
  headless: boolean,
  cdpUrl?: string,
): Promise<{
  context: BrowserContext;
  close(): Promise<void>;
}> {
  if (cdpUrl) {
    // Real-mode approval ledgers still live in the dedicated directory even
    // though Chrome itself is user-owned. Create and validate it before a
    // task can be leased so reserveAttempt never fails after browser use.
    await secureDirectory(directory);
    try {
      const browser = await chromium.connectOverCDP(cdpUrl, { timeout: 30_000 });
      const context = browser.contexts()[0];
      if (!context) {
        throw new CollectorError("browser_failed");
      }
      return {
        context,
        async close() {
          // Do not close the user-owned Chrome process. The connection is
          // released when the Collector process exits.
        },
      };
    } catch (error) {
      if (error instanceof CollectorError) throw error;
      throw new CollectorError("browser_failed");
    }
  }
  const unlock = await lockProfile(directory);
  try {
    const context = await chromium.launchPersistentContext(directory, {
      headless,
      timeout: 30_000,
      // Explicit direct connection; no inherited system proxy or stealth switches.
      args: ["--no-proxy-server"],
      env: allowlistedEnvironment(),
      // Callers own SIGINT/SIGTERM and close the session themselves. Playwright's own SIGINT
      // handler would exit(130) right after closing the browser, before `close()` releases the
      // profile and spool locks, leaving stale `.collector.lock` files behind.
      handleSIGINT: false,
      handleSIGTERM: false,
    });
    let closed = false;
    return {
      context,
      async close() {
        if (closed) return;
        closed = true;
        try {
          await context.close();
        } finally {
          await unlock();
        }
      },
    };
  } catch {
    await unlock();
    throw new CollectorError("browser_failed");
  }
}
