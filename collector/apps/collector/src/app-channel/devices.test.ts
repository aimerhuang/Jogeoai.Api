import { describe, expect, it } from "vitest";
import { parseBindings } from "./bindings.ts";
import {
  DeviceInspector,
  parseAdbDevices,
  parseAvdName,
  parseInstalledVersion,
} from "./devices.ts";
import {
  DEVICE_A,
  DEVICE_B,
  bindingsFixture,
  emulatorA,
  emulatorB,
  fakeAdb,
  type FakeEmulator,
} from "./test-fixtures.ts";

const declared = parseBindings(bindingsFixture()).devices;
const inspector = (emulators: FakeEmulator[], extraSerials: string[] = []) => {
  const runner = fakeAdb(emulators, extraSerials);
  return { runner, inspector: new DeviceInspector({ runner, devices: declared }) };
};

describe("adb output parsing", () => {
  it("lists only online emulator serials", () => {
    expect(
      parseAdbDevices(
        "List of devices attached\nemulator-5556\tdevice\nemulator-5558\toffline\nR58M123ABC\tdevice\n\n",
      ),
    ).toEqual(["emulator-5556"]);
  });
  it("reads the AVD name only from a complete console answer", () => {
    expect(parseAvdName("AI_Apps_4KB\r\nOK\r\n")).toBe("AI_Apps_4KB");
    expect(parseAvdName("KO: unknown command\r\n")).toBeUndefined();
    expect(parseAvdName("")).toBeUndefined();
  });
  it("reads the version of the requested package block only", () => {
    const dump = [
      "Packages:",
      "  Package [com.other.app] (1a2b3c):",
      "    versionName=9.9.9",
      "  Package [com.larus.nova] (980f6c):",
      "    versionCode=15070040 minSdk=21 targetSdk=35",
      "    versionName=15.7.0",
    ].join("\n");
    expect(parseInstalledVersion(dump, "com.larus.nova")).toBe("15.7.0");
    expect(parseInstalledVersion(dump, "com.other.app")).toBe("9.9.9");
    expect(
      parseInstalledVersion("Unable to find package: com.larus.nova\n", "com.larus.nova"),
    ).toBe(null);
  });
});

describe("device inspection", () => {
  it("checks online identity and boot only on the requested device, without any App/config preflight", async () => {
    const { runner, inspector: devices } = inspector([emulatorA({ timezone: "UTC" }), emulatorB()]);
    expect(await devices.onlineDevices(DEVICE_A)).toEqual(new Map([[DEVICE_A, "emulator-5556"]]));
    expect(runner.calls.filter((call) => call[2] === "shell")).toEqual([
      ["-s", "emulator-5556", "shell", "getprop", "sys.boot_completed"],
    ]);
  });
  it("reports independent online devices and excludes a duplicate identity or a booting device", async () => {
    const { inspector: devices } = inspector([
      emulatorA(),
      emulatorA({ serial: "emulator-5562" }),
      emulatorB(),
    ]);
    expect(await devices.onlineDevices()).toEqual(new Map([[DEVICE_B, "emulator-5558"]]));
    expect(
      await inspector([emulatorA({ bootCompleted: "0" }), emulatorB()]).inspector.onlineDevices(
        DEVICE_A,
      ),
    ).toEqual(new Map());
  });
  it("resolves AVD names to serials and reports matching devices as healthy", async () => {
    const { inspector: devices } = inspector([emulatorA(), emulatorB()]);
    const states = await devices.inspectAll();
    expect(states.get(DEVICE_A)).toEqual({
      deviceId: DEVICE_A,
      serial: "emulator-5556",
      bootCompleted: true,
      actual: {
        androidVersion: "15",
        abi: "arm64-v8a",
        pageSize: "4k",
        locale: "zh-Hans-CN",
        timezone: "Asia/Shanghai",
      },
      mismatches: [],
      failure: null,
    });
    expect(states.get(DEVICE_B)?.serial).toBe("emulator-5558");
    expect(devices.serialOf(DEVICE_B)).toBe("emulator-5558");
  });
  it("follows a device whose serial changed after an emulator restart", async () => {
    const { inspector: devices } = inspector([
      emulatorA({ serial: "emulator-5560" }),
      emulatorB({ serial: "emulator-5554" }),
    ]);
    const states = await devices.inspectAll();
    expect(states.get(DEVICE_A)?.serial).toBe("emulator-5560");
    expect(states.get(DEVICE_B)?.serial).toBe("emulator-5554");
  });
  it("reports device_config_mismatch with the fields that differ", async () => {
    // The emulator re-syncs its time zone from the host on every start.
    const { inspector: devices } = inspector([
      emulatorA({ timezone: "Asia/Taipei" }),
      emulatorB({ locales: "en-US,zh-Hans-CN", pageSize: "16384", release: "14", abi: "x86_64" }),
    ]);
    const states = await devices.inspectAll();
    expect(states.get(DEVICE_A)).toMatchObject({
      failure: "device_config_mismatch",
      mismatches: ["timezone"],
      actual: { timezone: "Asia/Taipei" },
    });
    expect(states.get(DEVICE_B)).toMatchObject({
      failure: "device_config_mismatch",
      mismatches: ["androidVersion", "abi", "pageSize", "locale"],
      actual: { androidVersion: "14", abi: "x86_64", pageSize: "16k", locale: "en-US" },
    });
  });
  it("reports device_offline for a missing, offline or still booting emulator", async () => {
    const missing = await inspector([emulatorA()]).inspector.inspectAll();
    expect(missing.get(DEVICE_B)).toMatchObject({ serial: null, failure: "device_offline" });
    const offline = await inspector([
      emulatorA(),
      emulatorB({ state: "offline" }),
    ]).inspector.inspectAll();
    expect(offline.get(DEVICE_B)).toMatchObject({ serial: null, failure: "device_offline" });
    const booting = await inspector([
      emulatorA(),
      emulatorB({ bootCompleted: "" }),
    ]).inspector.inspectAll();
    expect(booting.get(DEVICE_B)).toMatchObject({
      serial: "emulator-5558",
      bootCompleted: false,
      failure: "device_offline",
    });
    expect(booting.get(DEVICE_A)?.failure).toBeNull();
  });
  it("treats two emulators with one AVD name as offline instead of guessing", async () => {
    const states = await inspector([
      emulatorA(),
      emulatorA({ serial: "emulator-5562" }),
      emulatorB(),
    ]).inspector.inspectAll();
    expect(states.get(DEVICE_A)).toMatchObject({ serial: null, failure: "device_offline" });
    expect(states.get(DEVICE_B)?.failure).toBeNull();
  });
  it("never sends a guest command to an undeclared emulator or a physical device", async () => {
    const { runner, inspector: devices } = inspector(
      [emulatorA(), emulatorB(), { serial: "emulator-5570", avd: "Personal_Pixel" }],
      ["R58M123ABC"],
    );
    await devices.inspectAll();
    await devices.installedAppVersion(DEVICE_A, "com.larus.nova");
    const addressed = (serial: string) => runner.calls.filter((call) => call[1] === serial);
    // Only the console name query, which does not enter the guest.
    expect(addressed("emulator-5570")).toEqual([["-s", "emulator-5570", "emu", "avd", "name"]]);
    expect(addressed("R58M123ABC")).toEqual([]);
    await expect(
      devices.installedAppVersion("Personal_Pixel", "com.larus.nova"),
    ).rejects.toMatchObject({ code: "device_offline" });
  });
  it("issues read-only commands only", async () => {
    const { runner, inspector: devices } = inspector([emulatorA(), emulatorB()]);
    await devices.inspectAll();
    await devices.installedAppVersion(DEVICE_A, "com.larus.nova");
    const guest = runner.calls.filter((call) => call[2] === "shell").map((call) => call[3]);
    expect(new Set(guest)).toEqual(new Set(["getprop", "getconf", "settings", "dumpsys"]));
    expect(
      runner.calls.filter((call) => call[3] === "settings").every((call) => call[4] === "get"),
    ).toBe(true);
  });
  it("reads installed versions and null for a missing package", async () => {
    const { inspector: devices } = inspector([emulatorA(), emulatorB()]);
    await devices.inspectAll();
    expect(await devices.installedAppVersion(DEVICE_A, "com.larus.nova")).toBe("15.7.0");
    expect(await devices.installedAppVersion(DEVICE_A, "com.not.installed")).toBeNull();
  });
  it("fails as adb_unavailable when adb itself cannot list devices", async () => {
    const devices = new DeviceInspector({
      devices: declared,
      runner: {
        run: async () => ({ code: null, stdout: "", stderr: "" }),
        start: () => {
          throw new Error("unused");
        },
      },
    });
    await expect(devices.inspectAll()).rejects.toMatchObject({ code: "adb_unavailable" });
  });
});
