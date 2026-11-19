import type { DeviceDeclaration } from "./bindings.ts";
import type { CommandResult, CommandRunner } from "./command.ts";
import { AppChannelError, AppSetupError, type AppFailureCode } from "./failure.ts";

// adb access to the declared emulators (design §3). A device is identified by its AVD name; the
// serial is looked up on every inspection because it changes with the emulator port. Nothing
// here changes a device: properties are read and compared, never set.

export interface DeviceActual {
  androidVersion: string;
  abi: string;
  pageSize: string;
  locale: string;
  timezone: string;
}
export type DeviceField = keyof DeviceActual;
const DEVICE_FIELDS: readonly DeviceField[] = [
  "androidVersion",
  "abi",
  "pageSize",
  "locale",
  "timezone",
];

export interface DeviceState {
  deviceId: string;
  // null: no running emulator reports this AVD name (or two do).
  serial: string | null;
  bootCompleted: boolean;
  actual: DeviceActual | null;
  // Declared fields whose actual value differs.
  mismatches: DeviceField[];
  failure: Extract<AppFailureCode, "device_offline" | "device_config_mismatch"> | null;
}

export interface DeviceInspectorOptions {
  runner: CommandRunner;
  devices: readonly DeviceDeclaration[];
  adbPath?: string;
  timeoutMs?: number;
}

const EMULATOR_SERIAL = /^emulator-\d{4,5}$/u;
const PAGE_SIZES: Record<string, string> = { "4096": "4k", "16384": "16k" };

// `adb devices`: one "<serial>\t<state>" line per device after a header line.
export function parseAdbDevices(output: string): string[] {
  return output
    .split(/\r?\n/u)
    .map((line) => /^(\S+)\t(\S+)$/u.exec(line.trim()))
    .filter((match): match is RegExpExecArray => match !== null && match[2] === "device")
    .map((match) => match[1]!)
    .filter((serial) => EMULATOR_SERIAL.test(serial));
}
// `adb emu avd name`: the AVD name, then "OK".
export function parseAvdName(output: string): string | undefined {
  const lines = output
    .split(/\r?\n/u)
    .map((line) => line.trim())
    .filter(Boolean);
  return lines.length === 2 && lines[1] === "OK" ? lines[0] : undefined;
}
// `dumpsys package <pkg>`: the versionName of that package's own "Package [<pkg>]" block.
export function parseInstalledVersion(output: string, packageName: string): string | null {
  const lines = output.split(/\r?\n/u);
  const start = lines.findIndex((line) => line.trim().startsWith(`Package [${packageName}]`));
  if (start === -1) return null;
  for (const line of lines.slice(start + 1)) {
    if (/^\s*Package \[/u.test(line)) break;
    const match = /^\s*versionName=(\S+)/u.exec(line);
    if (match) return match[1]!;
  }
  return null;
}

export class DeviceInspector {
  private readonly runner: CommandRunner;
  private readonly declared: ReadonlyMap<string, DeviceDeclaration>;
  private readonly adbPath: string;
  private readonly timeoutMs: number;
  private serials = new Map<string, string>();

  constructor(options: DeviceInspectorOptions) {
    this.runner = options.runner;
    this.declared = new Map(options.devices.map((device) => [device.id, device]));
    this.adbPath = options.adbPath ?? "adb";
    this.timeoutMs = options.timeoutMs ?? 15_000;
  }

  // Serial of a declared device as found by the last `inspectAll`.
  serialOf(deviceId: string): string | undefined {
    return this.serials.get(deviceId);
  }

  // Lists running emulators, asks each for its AVD name (an emulator-console query that does not
  // enter the guest) and from then on talks only to those whose name is declared. Physical
  // devices and undeclared emulators never receive a shell command.
  private async resolveSerials(): Promise<Map<string, string>> {
    const listed = await this.adb(["devices"]);
    if (listed.code !== 0) throw new AppSetupError("adb_unavailable");
    const found = new Map<string, string[]>();
    await Promise.all(
      parseAdbDevices(listed.stdout).map(async (serial) => {
        const named = await this.adb(["-s", serial, "emu", "avd", "name"]);
        const name = named.code === 0 ? parseAvdName(named.stdout) : undefined;
        if (name !== undefined && this.declared.has(name))
          found.set(name, [...(found.get(name) ?? []), serial]);
      }),
    );
    this.serials = new Map(
      [...found].flatMap(([name, serials]) =>
        // Two emulators claiming one AVD name cannot be told apart: treat the device as offline.
        serials.length === 1 ? [[name, serials[0]!] as const] : [],
      ),
    );
    return this.serials;
  }

  // device-v1 admission: only resolve the declared identity and confirm that Android is online.
  // Never read an App/version, account, UI, locale, or the trial's configuration/limit files.
  async onlineDevices(deviceId?: string): Promise<Map<string, string>> {
    const resolved = await this.resolveSerials();
    const online = await Promise.all(
      [...resolved]
        .filter(([id]) => !deviceId || id === deviceId)
        .map(async ([id, serial]) => {
          const boot = await this.adb(["-s", serial, "shell", "getprop", "sys.boot_completed"]);
          return boot.code === 0 && boot.stdout.trim() === "1" ? [[id, serial] as const] : [];
        }),
    );
    return new Map(online.flat());
  }

  async inspectAll(deviceId?: string): Promise<Map<string, DeviceState>> {
    const resolved = await this.resolveSerials();
    const states = await Promise.all(
      [...this.declared.values()]
        .filter((device) => !deviceId || device.id === deviceId)
        .map((device) => this.inspect(device, resolved.get(device.id) ?? null)),
    );
    return new Map(states.map((state) => [state.deviceId, state]));
  }

  // Installed versionName of a package on a declared, online device; null when not installed.
  async installedAppVersion(
    deviceId: string,
    packageName: string,
    expectedSerial?: string,
  ): Promise<string | null> {
    const serial = this.serials.get(deviceId);
    if (
      !this.declared.has(deviceId) ||
      !serial ||
      (expectedSerial !== undefined && serial !== expectedSerial)
    )
      throw new AppChannelError("device_offline");
    const result = await this.adb(["-s", serial, "shell", "dumpsys", "package", packageName]);
    if (result.code !== 0) throw new AppChannelError("device_offline");
    return parseInstalledVersion(result.stdout, packageName);
  }

  private async inspect(device: DeviceDeclaration, serial: string | null): Promise<DeviceState> {
    const offline: DeviceState = {
      deviceId: device.id,
      serial,
      bootCompleted: false,
      actual: null,
      mismatches: [],
      failure: "device_offline",
    };
    if (serial === null) return offline;
    const read = async (args: string[]) => {
      const result = await this.adb(["-s", serial, "shell", ...args]);
      return result.code === 0 ? result.stdout.trim() : undefined;
    };
    if ((await read(["getprop", "sys.boot_completed"])) !== "1") return offline;
    const [androidVersion, abi, pageSize, locales, timezone] = await Promise.all([
      read(["getprop", "ro.build.version.release"]),
      read(["getprop", "ro.product.cpu.abi"]),
      read(["getconf", "PAGE_SIZE"]),
      read(["settings", "get", "system", "system_locales"]),
      read(["getprop", "persist.sys.timezone"]),
    ]);
    if (
      androidVersion === undefined ||
      abi === undefined ||
      pageSize === undefined ||
      locales === undefined ||
      timezone === undefined
    )
      return offline;
    const actual: DeviceActual = {
      androidVersion,
      abi,
      pageSize: PAGE_SIZES[pageSize] ?? pageSize,
      // The first entry of the locale list is the language apps render in.
      locale: locales.split(",")[0]!.trim(),
      timezone,
    };
    const mismatches = DEVICE_FIELDS.filter((field) => actual[field] !== device[field]);
    return {
      deviceId: device.id,
      serial,
      bootCompleted: true,
      actual,
      mismatches,
      failure: mismatches.length > 0 ? "device_config_mismatch" : null,
    };
  }

  private adb(args: string[]): Promise<CommandResult> {
    return this.runner.run(this.adbPath, args, { timeoutMs: this.timeoutMs });
  }
}
