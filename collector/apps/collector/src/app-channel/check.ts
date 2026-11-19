import type { AppPlatformCode, ResolvedBinding } from "./bindings.ts";
import type { DeviceField, DeviceInspector, DeviceState } from "./devices.ts";
import { AppChannelError, type AppFailureCode } from "./failure.ts";

// Read-only self-check (PRD FR-A2): one conclusion per binding, and a failing binding never
// changes another binding's conclusion. It reads device and package facts only: no App is
// opened, no recording is started and no question is sent.

export type CheckReason =
  | AppFailureCode
  // Check-only reasons; they are never written to a record.
  | "app_not_installed"
  | "login_probe_failed";
export interface DeviceMismatch {
  field: DeviceField;
  declared: string;
  actual: string;
}
export interface BindingCheckResult {
  device: string;
  platform: AppPlatformCode;
  accountLabel: string;
  package: string;
  status: "pass" | "fail" | "disabled";
  reason?: CheckReason;
  disabledReason?: string;
  adbSerial?: string;
  reviewedAppVersion: string;
  installedAppVersion?: string | null;
  mismatches?: DeviceMismatch[];
  // `not_checked` until a login probe is supplied (it needs the stage-4 driver and contract).
  login: "not_checked" | "logged_in" | "failed";
}

// Decides whether the App of one binding is logged in. Visibility only: it never interacts with
// a login or verification screen. Resolves with "logged_in" or the failure class it saw.
export type LoginProbe = (
  binding: ResolvedBinding,
  device: DeviceState,
) => Promise<"logged_in" | AppFailureCode>;

export interface CheckOptions {
  bindings: readonly ResolvedBinding[];
  inspector: Pick<DeviceInspector, "inspectAll" | "installedAppVersion">;
  probeLogin?: LoginProbe;
}

export async function checkBindings(options: CheckOptions): Promise<BindingCheckResult[]> {
  const devices = await options.inspector.inspectAll();
  const results: BindingCheckResult[] = [];
  for (const binding of options.bindings)
    results.push(await checkBinding(binding, devices.get(binding.device.id), options));
  return results;
}

async function checkBinding(
  binding: ResolvedBinding,
  device: DeviceState | undefined,
  options: CheckOptions,
): Promise<BindingCheckResult> {
  const base = {
    device: binding.device.id,
    platform: binding.platformCode,
    accountLabel: binding.accountLabel,
    package: binding.package,
    reviewedAppVersion: binding.reviewedAppVersion,
    login: "not_checked",
  } as const;
  // A disabled binding is reported with its reason and is not a failure.
  if (!binding.enabled)
    return { ...base, status: "disabled", disabledReason: binding.disabledReason };

  const fail = (reason: CheckReason, extra: Partial<BindingCheckResult> = {}) =>
    ({ ...base, ...extra, status: "fail", reason }) satisfies BindingCheckResult;
  if (!device || device.serial === null || device.failure === "device_offline")
    return fail("device_offline");
  const online = { adbSerial: device.serial };
  // Any declared property that differs fails every binding of the device.
  if (device.failure === "device_config_mismatch")
    return fail("device_config_mismatch", {
      ...online,
      mismatches: device.mismatches.map((field) => ({
        field,
        declared: binding.device[field],
        actual: device.actual?.[field] ?? "",
      })),
    });

  let installedAppVersion: string | null;
  try {
    installedAppVersion = await options.inspector.installedAppVersion(
      binding.device.id,
      binding.package,
    );
  } catch (error) {
    return fail(error instanceof AppChannelError ? error.code : "device_offline", online);
  }
  const facts = { ...online, installedAppVersion };
  if (installedAppVersion === null) return fail("app_not_installed", facts);
  // The reviewed screens are only known for the reviewed version.
  if (installedAppVersion !== binding.reviewedAppVersion)
    return fail("app_contract_unverified", facts);

  if (!options.probeLogin) return { ...base, ...facts, status: "pass" };
  let login: Awaited<ReturnType<LoginProbe>>;
  try {
    login = await options.probeLogin(binding, device);
  } catch (error) {
    return fail(error instanceof AppChannelError ? error.code : "login_probe_failed", {
      ...facts,
      login: "failed",
    });
  }
  return login === "logged_in"
    ? { ...base, ...facts, status: "pass", login: "logged_in" }
    : fail(login, { ...facts, login: "failed" });
}
