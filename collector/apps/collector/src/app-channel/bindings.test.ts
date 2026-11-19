import { mkdtemp, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import {
  DEFAULT_BINDINGS_FILE,
  DEFAULT_BINDING_LIMITS,
  DEFAULT_PLATFORM_LIMITS,
  bindingsFilePath,
  loadBindingsFile,
  parseBindings,
  resolveBindings,
} from "./bindings.ts";
import { AppSetupError } from "./failure.ts";
import { DEVICE_A, DEVICE_B, bindingsFixture } from "./test-fixtures.ts";

const directories: string[] = [];
afterEach(async () => {
  for (const directory of directories.splice(0))
    await rm(directory, { recursive: true, force: true });
});
async function bindingsFile(content: unknown): Promise<string> {
  const directory = await mkdtemp(join(tmpdir(), "aivc-bindings-"));
  directories.push(directory);
  const path = join(directory, "app-bindings.json");
  await writeFile(path, typeof content === "string" ? content : JSON.stringify(content));
  return path;
}

type Fixture = ReturnType<typeof bindingsFixture>;
// Paths of the issues a mutated fixture is rejected with.
function rejected(mutate: (config: Fixture) => void): string[] {
  const config = bindingsFixture();
  mutate(config);
  try {
    parseBindings(config);
  } catch (error) {
    expect(error).toBeInstanceOf(AppSetupError);
    expect((error as AppSetupError).code).toBe("bindings_invalid");
    return (error as AppSetupError).issues.map((issue) => issue.path);
  }
  throw new Error("the configuration was accepted");
}

describe("ownership configuration schema", () => {
  it("accepts the fixture and resolves bindings with their device, package and limits", () => {
    const resolved = resolveBindings(parseBindings(bindingsFixture()));
    expect(resolved).toHaveLength(4);
    expect(resolved[0]).toMatchObject({
      key: `${DEVICE_A}/doubao`,
      device: { id: DEVICE_A, locale: "zh-Hans-CN", timezone: "Asia/Shanghai" },
      platformCode: "doubao",
      package: "com.larus.nova",
      reviewedAppVersion: "15.7.0",
      accountLabel: "doubao-a",
      enabled: true,
      limits: { minIntervalSeconds: 600, hourlyQuota: 4, dailyQuota: 30 },
      platformLimits: { minIntervalSeconds: 120, dailyCap: 120 },
    });
  });
  it("defaults omitted limits to 100 s / 30 s without counts (KD-21)", () => {
    const config = bindingsFixture();
    for (const binding of config.bindings) {
      delete binding.minIntervalSeconds;
      delete binding.hourlyQuota;
      delete binding.dailyQuota;
    }
    config.platforms.doubao = { package: "com.larus.nova" } as never;
    const [first] = resolveBindings(parseBindings(config));
    expect(first?.limits).toEqual(DEFAULT_BINDING_LIMITS);
    expect(first?.limits).toEqual({ minIntervalSeconds: 100, hourlyQuota: null, dailyQuota: null });
    expect(first?.platformLimits).toEqual(DEFAULT_PLATFORM_LIMITS);
    expect(first?.platformLimits).toEqual({ minIntervalSeconds: 30, dailyCap: null });
  });
  it("accepts explicit limits, including counts", () => {
    const config = bindingsFixture();
    Object.assign(config.bindings[0]!, { minIntervalSeconds: 900, hourlyQuota: 2, dailyQuota: 10 });
    Object.assign(config.platforms.doubao!, { minIntervalSeconds: 300, dailyCap: 40 });
    const [first] = resolveBindings(parseBindings(config));
    expect(first?.limits).toEqual({ minIntervalSeconds: 900, hourlyQuota: 2, dailyQuota: 10 });
    expect(first?.platformLimits).toEqual({ minIntervalSeconds: 300, dailyCap: 40 });
  });

  // AC-A2: the three named invalid configurations.
  it("rejects an unknown platform code", () => {
    expect(
      rejected((config) => {
        config.platforms.not_a_platform = { package: "com.example.app" } as never;
      }),
    ).toEqual(["platforms.not_a_platform"]);
    expect(
      rejected((config) => {
        config.bindings[0]!.platform = "not_a_platform";
      }),
    ).toEqual(["bindings.0.platform"]);
  });
  it("rejects two bindings for one device and platform", () => {
    expect(
      rejected((config) => {
        config.bindings.push({ ...config.bindings[0]!, accountLabel: "doubao-c" });
      }),
    ).toEqual(["bindings.4.platform"]);
  });
  it("rejects one account label on two devices", () => {
    expect(
      rejected((config) => {
        config.bindings[2]!.accountLabel = "doubao-a";
      }),
    ).toEqual(["bindings.2.accountLabel"]);
    // Labels are unique across the whole file, not only across devices.
    expect(
      rejected((config) => {
        config.bindings[1]!.accountLabel = "doubao-a";
      }),
    ).toEqual(["bindings.1.accountLabel"]);
  });

  it("rejects intervals below the control plane's lower bounds and non-positive counts", () => {
    const invalid: [string, (config: Fixture) => void][] = [
      ["bindings.0.minIntervalSeconds", (c) => void (c.bindings[0]!.minIntervalSeconds = 59)],
      ["bindings.0.hourlyQuota", (c) => void (c.bindings[0]!.hourlyQuota = 0)],
      ["bindings.0.dailyQuota", (c) => void (c.bindings[0]!.dailyQuota = -1)],
      [
        "platforms.doubao.minIntervalSeconds",
        (c) => void (c.platforms.doubao!.minIntervalSeconds = 9),
      ],
      ["platforms.doubao.dailyCap", (c) => void (c.platforms.doubao!.dailyCap = 0)],
    ];
    for (const [path, mutate] of invalid) expect(rejected(mutate)).toEqual([path]);
    // The old Web-derived caps no longer bind the App channel.
    const config = bindingsFixture();
    Object.assign(config.bindings[0]!, { minIntervalSeconds: 100, hourlyQuota: 40 });
    Object.assign(config.platforms.doubao!, { minIntervalSeconds: 30, dailyCap: 2_000 });
    expect(() => parseBindings(config)).not.toThrow();
  });
  it("requires a reason for a disabled binding and refuses one on an enabled binding", () => {
    expect(rejected((c) => void (c.bindings[0]!.enabled = false))).toEqual([
      "bindings.0.disabledReason",
    ]);
    expect(rejected((c) => void (c.bindings[0]!.disabledReason = "left over"))).toEqual([
      "bindings.0.disabledReason",
    ]);
    const config = bindingsFixture();
    Object.assign(config.bindings[0]!, { enabled: false, disabledReason: "not readable" });
    expect(resolveBindings(parseBindings(config))[0]).toMatchObject({
      enabled: false,
      disabledReason: "not readable",
    });
  });
  it("rejects bindings that refer to an undeclared device or platform", () => {
    expect(rejected((c) => void (c.bindings[0]!.device = "Other_AVD"))).toEqual([
      "bindings.0.device",
    ]);
    expect(rejected((c) => void (c.bindings[0]!.platform = "qwen"))).toEqual([
      "bindings.0.platform",
    ]);
  });
  it("rejects duplicate devices and a package shared by two platforms", () => {
    expect(rejected((c) => void c.devices.push({ ...c.devices[0]! }))).toEqual(["devices.2.id"]);
    expect(rejected((c) => void (c.platforms.kimi!.package = "com.larus.nova"))).toEqual([
      "platforms.kimi.package",
    ]);
  });
  it("rejects account labels that are not code names", () => {
    // A phone number fails both the shape rule and the digit-run rule; the path is the same.
    for (const label of ["Doubao-A", "doubao_a", "13800138000", "user-13800138000", "-a", "a b"])
      expect(new Set(rejected((c) => void (c.bindings[0]!.accountLabel = label)))).toEqual(
        new Set(["bindings.0.accountLabel"]),
      );
  });
  it("rejects unknown keys, other page sizes and invalid device declarations", () => {
    expect(rejected((c) => void ((c as Record<string, unknown>).extra = 1))).toEqual([""]);
    expect(rejected((c) => void (c.bindings[0]!.password = "x"))).toEqual(["bindings.0"]);
    expect(rejected((c) => void (c.devices[0]!.pageSize = "16k"))).toEqual(["devices.0.pageSize"]);
    expect(rejected((c) => void (c.devices[0]!.timezone = "Mars/Olympus"))).toEqual([
      "devices.0.timezone",
    ]);
    expect(rejected((c) => void (c.devices[0]!.locale = "简体中文"))).toEqual(["devices.0.locale"]);
    expect(rejected((c) => void (c.version = 2))).toEqual(["version"]);
  });
  it("never echoes the file's values in an issue", () => {
    const config = bindingsFixture();
    config.bindings[0]!.accountLabel = "13800138000";
    try {
      parseBindings(config);
      throw new Error("accepted");
    } catch (error) {
      expect(JSON.stringify((error as AppSetupError).issues)).not.toContain("13800138000");
    }
  });
});

describe("bindings file loading", () => {
  it("loads a file and reports its hash", async () => {
    const content = JSON.stringify(bindingsFixture());
    const loaded = await loadBindingsFile(await bindingsFile(content));
    expect(loaded.config.bindings).toHaveLength(4);
    expect(loaded.sha256).toMatch(/^[0-9a-f]{64}$/u);
  });
  it("rejects relative paths, missing files, symlinks and invalid JSON", async () => {
    await expect(loadBindingsFile("app-bindings.json")).rejects.toMatchObject({
      code: "bindings_invalid",
    });
    const path = await bindingsFile(bindingsFixture());
    await expect(loadBindingsFile(`${path}.missing`)).rejects.toMatchObject({
      code: "bindings_invalid",
    });
    await symlink(path, `${path}.link`);
    await expect(loadBindingsFile(`${path}.link`)).rejects.toMatchObject({
      code: "bindings_invalid",
    });
    await expect(loadBindingsFile(await bindingsFile("{not json"))).rejects.toMatchObject({
      code: "bindings_invalid",
    });
  });
  it("uses the committed file unless APP_COLLECTOR_BINDINGS_FILE is set", () => {
    expect(bindingsFilePath({})).toBe(DEFAULT_BINDINGS_FILE);
    expect(DEFAULT_BINDINGS_FILE.endsWith("/apps/collector/app-bindings.json")).toBe(true);
    expect(bindingsFilePath({ APP_COLLECTOR_BINDINGS_FILE: "/aivc/other.json" })).toBe(
      "/aivc/other.json",
    );
  });
});

// AC-A1: the committed configuration matches the measured devices and Apps.
describe("committed app-bindings.json", () => {
  it("declares 2 devices, 7 platforms and 14 bindings with the measured packages and versions", async () => {
    const { config } = await loadBindingsFile(DEFAULT_BINDINGS_FILE);
    expect(config.devices.map((device) => device.id)).toEqual([DEVICE_A, DEVICE_B]);
    for (const device of config.devices)
      expect(device).toEqual({
        id: device.id,
        androidVersion: "15",
        abi: "arm64-v8a",
        pageSize: "4k",
        locale: "zh-Hans-CN",
        timezone: "Asia/Shanghai",
      });
    const measured = {
      doubao: ["com.larus.nova", "15.7.0"],
      ernie: ["com.baidu.newapp", "5.22.0.10"],
      zhipu_qingyan: ["com.zhipuai.qingyan", "3.8.7"],
      qwen: ["com.aliyun.tongyi", "7.6.5.3101"],
      kimi: ["com.moonshot.kimichat", "3.1.8"],
      deepseek: ["com.deepseek.chat", "2.8.0"],
      tencent_yuanbao: ["com.tencent.hunyuan.app.chat", "2.92.0"],
    };
    const bindings = resolveBindings(config);
    expect(bindings).toHaveLength(14);
    for (const deviceId of [DEVICE_A, DEVICE_B]) {
      const onDevice = bindings.filter((binding) => binding.device.id === deviceId);
      expect(
        Object.fromEntries(
          onDevice.map((binding) => [
            binding.platformCode,
            [binding.package, binding.reviewedAppVersion],
          ]),
        ),
      ).toEqual(measured);
      // "-a" on the first device, "-b" on the second.
      const suffix = deviceId === DEVICE_A ? "-a" : "-b";
      expect(onDevice.every((binding) => binding.accountLabel.endsWith(suffix))).toBe(true);
    }
    // Wenxin (ernie) stays in the file but is stopped, with the finding that stopped it as its
    // reason.
    const disabled = bindings.filter((binding) => !binding.enabled);
    expect(disabled.map((binding) => binding.platformCode).sort()).toEqual(["ernie", "ernie"]);
    expect(
      disabled.every((binding) =>
        /driver-spike\.md|implement\.md/u.test(binding.disabledReason ?? ""),
      ),
    ).toBe(true);
    // KD-21 as changed on 2026-11-17: 100 s per account and platform, 30 s per platform, no counts.
    for (const binding of bindings) {
      expect(binding.limits).toEqual({
        minIntervalSeconds: 100,
        hourlyQuota: null,
        dailyQuota: null,
      });
      expect(binding.platformLimits).toEqual({ minIntervalSeconds: 30, dailyCap: null });
    }
  });
});
