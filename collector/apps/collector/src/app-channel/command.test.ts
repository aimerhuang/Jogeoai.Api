import { afterEach, describe, expect, it, vi } from "vitest";
import { systemCommandRunner } from "./command.ts";

// The real runner, exercised with the Node binary itself: no adb, no device, no network.
const node = process.execPath;
afterEach(() => vi.unstubAllEnvs());

describe("system command runner", () => {
  it("returns exit code and output without rejecting", async () => {
    expect(
      await systemCommandRunner.run(node, ["-e", "process.stdout.write('out'); process.exit(3)"]),
    ).toEqual({ code: 3, stdout: "out", stderr: "" });
    expect(await systemCommandRunner.run(node, ["-e", "console.log('ok')"])).toMatchObject({
      code: 0,
      stdout: "ok\n",
    });
  });
  it("reports a missing program and a timeout as code null", async () => {
    expect(await systemCommandRunner.run("/nonexistent/aivc-adb", ["devices"])).toMatchObject({
      code: null,
    });
    expect(
      await systemCommandRunner.run(node, ["-e", "setTimeout(() => {}, 30000)"], {
        timeoutMs: 200,
      }),
    ).toMatchObject({ code: null });
  });
  it("passes arguments without a shell", async () => {
    const result = await systemCommandRunner.run(node, [
      "-e",
      "process.stdout.write(process.argv[1])",
      "$(echo injected); *",
    ]);
    expect(result.stdout).toBe("$(echo injected); *");
  });
  it("never passes tokens or database settings to the program", async () => {
    vi.stubEnv("COLLECTOR_SERVICE_TOKEN", "test-token-only-not-real");
    vi.stubEnv("DATABASE_URL", "postgres://example.invalid/db");
    const result = await systemCommandRunner.run(node, [
      "-e",
      "process.stdout.write(JSON.stringify(Object.keys(process.env)))",
    ]);
    const keys = JSON.parse(result.stdout) as string[];
    expect(keys).not.toContain("COLLECTOR_SERVICE_TOKEN");
    expect(keys).not.toContain("DATABASE_URL");
    expect(keys).toContain("PATH");
  });
  it("adds only the named variables to a long-lived command's environment", async () => {
    vi.stubEnv("COLLECTOR_SERVICE_TOKEN", "test-token-only-not-real");
    // Exit code 7 only when the named variable arrived and the token did not.
    const script =
      "process.exit(process.env.APPIUM_HOME === '/opt/aivc' && !process.env.COLLECTOR_SERVICE_TOKEN && process.env.PATH ? 7 : 1)";
    const running = systemCommandRunner.start(node, ["-e", script], {
      env: { APPIUM_HOME: "/opt/aivc" },
    });
    expect(await running.exited).toEqual({ code: 7 });
  });
  it("starts a long-lived command and reports its exit", async () => {
    const running = systemCommandRunner.start(node, ["-e", "setTimeout(() => {}, 30000)"]);
    running.kill("SIGINT");
    expect(await running.exited).toEqual({ code: null });
    // Killing an ended command is a no-op.
    running.kill("SIGKILL");
    const finished = systemCommandRunner.start(node, ["-e", "process.exit(0)"]);
    expect(await finished.exited).toEqual({ code: 0 });
    const missing = systemCommandRunner.start("/nonexistent/aivc-adb", []);
    expect(await missing.exited).toEqual({ code: null });
  });
});
