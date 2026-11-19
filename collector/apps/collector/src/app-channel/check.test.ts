import { describe, expect, it, vi } from "vitest";
import { parseBindings, resolveBindings } from "./bindings.ts";
import { checkBindings, type LoginProbe } from "./check.ts";
import { DeviceInspector } from "./devices.ts";
import { AppChannelError } from "./failure.ts";
import {
  DEVICE_A,
  DEVICE_B,
  bindingsFixture,
  emulatorA,
  emulatorB,
  fakeAdb,
  type FakeEmulator,
} from "./test-fixtures.ts";

function check(
  emulators: FakeEmulator[],
  options: {
    mutate?: (config: ReturnType<typeof bindingsFixture>) => void;
    probeLogin?: LoginProbe;
  } = {},
) {
  const fixture = bindingsFixture();
  options.mutate?.(fixture);
  const config = parseBindings(fixture);
  const runner = fakeAdb(emulators);
  return {
    runner,
    results: checkBindings({
      bindings: resolveBindings(config),
      inspector: new DeviceInspector({ runner, devices: config.devices }),
      probeLogin: options.probeLogin,
    }),
  };
}
const statuses = async (results: ReturnType<typeof check>["results"]) =>
  (await results).map((result) => [result.device, result.platform, result.status, result.reason]);

describe("binding self-check", () => {
  it("passes healthy bindings and reports login as not_checked without a probe", async () => {
    const results = await check([emulatorA(), emulatorB()]).results;
    expect(results).toHaveLength(4);
    expect(results[0]).toEqual({
      device: DEVICE_A,
      platform: "doubao",
      accountLabel: "doubao-a",
      package: "com.larus.nova",
      reviewedAppVersion: "15.7.0",
      installedAppVersion: "15.7.0",
      adbSerial: "emulator-5556",
      status: "pass",
      login: "not_checked",
    });
    expect(results.every((result) => result.status === "pass")).toBe(true);
  });
  // AC-A8: a reviewed version that differs from the installed one stops that binding only.
  it("fails a binding whose installed version differs from the reviewed version", async () => {
    const { results } = check([emulatorA(), emulatorB()], {
      mutate: (config) => void (config.bindings[0]!.reviewedAppVersion = "15.6.0"),
    });
    expect(await statuses(results)).toEqual([
      [DEVICE_A, "doubao", "fail", "app_contract_unverified"],
      [DEVICE_A, "kimi", "pass", undefined],
      [DEVICE_B, "doubao", "pass", undefined],
      [DEVICE_B, "kimi", "pass", undefined],
    ]);
    expect((await results)[0]).toMatchObject({
      reviewedAppVersion: "15.6.0",
      installedAppVersion: "15.7.0",
    });
  });
  it("fails after an App update changed the installed version", async () => {
    const { results } = check([
      emulatorA({ packages: { "com.larus.nova": "15.8.0", "com.moonshot.kimichat": "3.1.8" } }),
      emulatorB(),
    ]);
    expect((await statuses(results))[0]).toEqual([
      DEVICE_A,
      "doubao",
      "fail",
      "app_contract_unverified",
    ]);
  });
  it("fails every binding of a device whose properties differ from its declaration", async () => {
    const { results } = check([emulatorA({ timezone: "Asia/Taipei" }), emulatorB()]);
    expect(await statuses(results)).toEqual([
      [DEVICE_A, "doubao", "fail", "device_config_mismatch"],
      [DEVICE_A, "kimi", "fail", "device_config_mismatch"],
      [DEVICE_B, "doubao", "pass", undefined],
      [DEVICE_B, "kimi", "pass", undefined],
    ]);
    expect((await results)[0]?.mismatches).toEqual([
      { field: "timezone", declared: "Asia/Shanghai", actual: "Asia/Taipei" },
    ]);
  });
  it("fails the bindings of an offline device without affecting the other device", async () => {
    const { results } = check([emulatorB()]);
    expect(await statuses(results)).toEqual([
      [DEVICE_A, "doubao", "fail", "device_offline"],
      [DEVICE_A, "kimi", "fail", "device_offline"],
      [DEVICE_B, "doubao", "pass", undefined],
      [DEVICE_B, "kimi", "pass", undefined],
    ]);
  });
  it("fails a binding whose App is not installed", async () => {
    const { results } = check([
      emulatorA({ packages: { "com.larus.nova": "15.7.0" } }),
      emulatorB(),
    ]);
    expect((await results)[1]).toMatchObject({
      status: "fail",
      reason: "app_not_installed",
      installedAppVersion: null,
    });
  });
  it("reports a disabled binding with its reason, not as a failure, and does not query its App", async () => {
    const { runner, results } = check([emulatorA(), emulatorB()], {
      mutate: (config) =>
        Object.assign(config.bindings[1]!, { enabled: false, disabledReason: "not readable" }),
    });
    expect((await results)[1]).toEqual({
      device: DEVICE_A,
      platform: "kimi",
      accountLabel: "kimi-a",
      package: "com.moonshot.kimichat",
      reviewedAppVersion: "3.1.8",
      status: "disabled",
      disabledReason: "not readable",
      login: "not_checked",
    });
    expect(
      runner.calls.filter(
        (call) => call[1] === "emulator-5556" && call.includes("com.moonshot.kimichat"),
      ),
    ).toEqual([]);
  });
  it("uses a supplied login probe only for bindings that passed the earlier checks", async () => {
    const probeLogin = vi.fn<LoginProbe>(async (binding) => {
      if (binding.key === `${DEVICE_A}/kimi`) return "login_required";
      if (binding.key === `${DEVICE_B}/kimi`) throw new AppChannelError("challenge_required");
      if (binding.key === `${DEVICE_B}/doubao`) throw new Error("driver exploded");
      return "logged_in";
    });
    const { results } = check([emulatorA(), emulatorB()], { probeLogin });
    expect((await results).map((result) => [result.status, result.reason, result.login])).toEqual([
      ["pass", undefined, "logged_in"],
      ["fail", "login_required", "failed"],
      ["fail", "login_probe_failed", "failed"],
      ["fail", "challenge_required", "failed"],
    ]);
    const offline = check([emulatorB()], {
      probeLogin: vi.fn<LoginProbe>(async () => "logged_in"),
    });
    expect((await offline.results).filter((result) => result.login === "logged_in")).toHaveLength(
      2,
    );
  });
});
