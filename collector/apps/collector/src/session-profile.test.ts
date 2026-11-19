import { access, chmod, mkdir, mkdtemp, readFile, realpath, rm, symlink } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir, homedir } from "node:os";
import { afterEach, describe, expect, it, vi } from "vitest";
import { lockProfile, openSession, secureDirectory } from "./session-profile.ts";
import { bindRealSpool } from "./spool-binding.ts";
import { fixtureConfig } from "./test-fixtures.ts";

const browser = vi.hoisted(() => {
  const context = { close: vi.fn(async () => undefined) };
  return { context, launchPersistentContext: vi.fn(async () => context) };
});
vi.mock("playwright", () => ({
  chromium: { launchPersistentContext: browser.launchPersistentContext },
}));

const temporary: string[] = [];
afterEach(async () => {
  for (const directory of temporary.splice(0))
    await rm(directory, { recursive: true, force: true });
});
async function directories() {
  const base = await mkdtemp(join(await realpath(tmpdir()), "collector-profile-"));
  temporary.push(base);
  return { base, path: join(base, "aivc", "profile") };
}
describe("dedicated profile and spool safety", () => {
  it("locks single-process access and does not print profile state", async () => {
    const { path } = await directories();
    const unlock = await lockProfile(path);
    await expect(lockProfile(path)).rejects.toMatchObject({ code: "profile_unavailable" });
    expect(JSON.parse(await readFile(join(path, ".collector.lock"), "utf8"))).toEqual({
      pid: process.pid,
    });
    await unlock();
    await (
      await lockProfile(path)
    )();
  });
  it("leaves SIGINT/SIGTERM to the caller so close() can release the profile lock", async () => {
    const { path } = await directories();
    const session = await openSession(path, true);
    // Playwright's default SIGINT handler exits(130) before close() runs and strands the lock.
    expect(browser.launchPersistentContext).toHaveBeenCalledWith(
      expect.any(String),
      expect.objectContaining({ handleSIGINT: false, handleSIGTERM: false }),
    );
    await access(join(path, ".collector.lock"));
    await session.close();
    expect(browser.context.close).toHaveBeenCalledOnce();
    await expect(access(join(path, ".collector.lock"))).rejects.toThrow();
  });
  it("rejects home, relative, symlinked and in-repository profiles", async () => {
    const { base, path } = await directories();
    await expect(secureDirectory(homedir())).rejects.toThrow();
    await expect(secureDirectory("aivc/profile")).rejects.toThrow();
    await mkdir(join(base, "real"), { mode: 0o700 });
    await symlink(join(base, "real"), join(base, "aivc"));
    await expect(secureDirectory(path)).rejects.toThrow();
    const other = await directories();
    await mkdir(join(other.base, ".git"));
    await expect(secureDirectory(other.path)).rejects.toThrow();
  });
  it("refuses permissive existing directories instead of silently taking ownership", async () => {
    const { path } = await directories();
    await mkdir(path, { recursive: true, mode: 0o700 });
    await chmod(path, 0o755);
    await expect(secureDirectory(path)).rejects.toThrow();
  });
  it("binds spool to mode, API and project and rejects cross-project reuse", async () => {
    const { path } = await directories();
    await secureDirectory(path);
    const config = {
      ...fixtureConfig(),
      COLLECTOR_MODE: "real" as const,
      COLLECTOR_SPOOL_DIRECTORY: path,
    };
    await bindRealSpool(config);
    await bindRealSpool(config);
    await expect(
      bindRealSpool({ ...config, COLLECTOR_API_URL: "http://127.0.0.1:4312" }),
    ).rejects.toThrow("task_not_approved");
  });
});
