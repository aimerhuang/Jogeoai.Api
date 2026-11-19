import { createHash } from "node:crypto";
import { lstat, readFile } from "node:fs/promises";
import { isAbsolute } from "node:path";
import { fileURLToPath } from "node:url";
import { platformCodeSchema } from "@aivc/contracts";
import { z } from "zod";
import { AppSetupError, setupIssues } from "./failure.ts";

// Ownership configuration (PRD FR-A1, design §3): which App on which emulator belongs to which
// platform and account. It is the only source for "open which App on which device" and holds no
// sensitive data, so it is committed. Loading never contacts a device.

// Send limits (PRD KD-21 as changed on 2026-11-17, task 11-16-app-collector-control-plane design §8):
// one account on one platform at most once every 100 seconds, one platform (all devices together)
// at most once every 30 seconds, and no hourly or daily count unless the file sets one. The lower
// bounds are the control plane's own (Jogeoai.Api AppAdminRules); in the control-plane mode the
// larger of this file's interval and the control plane's applies.
export const DEFAULT_BINDING_LIMITS = {
  minIntervalSeconds: 100,
  hourlyQuota: null,
  dailyQuota: null,
} as const;
export const DEFAULT_PLATFORM_LIMITS = { minIntervalSeconds: 30, dailyCap: null } as const;
export const MIN_BINDING_INTERVAL_SECONDS = 60;
export const MIN_PLATFORM_INTERVAL_SECONDS = 10;
const MAX_INTERVAL_SECONDS = 86_400;

function isTimezone(value: string): boolean {
  try {
    new Intl.DateTimeFormat("en-US", { timeZone: value });
    return true;
  } catch {
    return false;
  }
}

// What a package name, an App version and a system language look like. The App screen contract
// (contract.ts) uses the same definitions, so a value this file accepts is never refused there.
export const packageNameSchema = z
  .string()
  .max(128)
  .regex(/^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$/u);
export const appVersionSchema = z.string().regex(/^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$/u);
export const localeSchema = z.string().regex(/^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$/u);

export const deviceDeclarationSchema = z
  .object({
    // AVD name: the stable device identity. adb serials change with the emulator port and are
    // only ever a runtime lookup result.
    id: z.string().regex(/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/u),
    androidVersion: z.string().regex(/^\d{1,2}(\.\d{1,2}){0,2}$/u),
    abi: z.string().regex(/^[a-z0-9][a-z0-9_-]{1,31}$/u),
    // PRD constraint: every device uses a 4 KB page-size system image.
    pageSize: z.literal("4k"),
    locale: localeSchema,
    timezone: z.string().max(64).refine(isTimezone, "must be an IANA time zone"),
  })
  .strict();
export type DeviceDeclaration = z.infer<typeof deviceDeclarationSchema>;

const platformDeclarationSchema = z
  .object({
    package: packageNameSchema,
    // Both devices together.
    minIntervalSeconds: z
      .number()
      .int()
      .min(MIN_PLATFORM_INTERVAL_SECONDS)
      .max(MAX_INTERVAL_SECONDS)
      .default(DEFAULT_PLATFORM_LIMITS.minIntervalSeconds),
    // Optional: Asia/Shanghai calendar-day cap of the platform, all devices together.
    dailyCap: z.number().int().min(1).max(100_000).optional(),
  })
  .strict();

const bindingSchema = z
  .object({
    device: z.string().min(1).max(64),
    platform: platformCodeSchema,
    // The App version whose screens an operator reviewed. Any other installed version means
    // `app_contract_unverified` until the review is repeated and this value updated.
    reviewedAppVersion: appVersionSchema,
    // A code name only. Starting with a letter and refusing long digit runs keeps phone numbers
    // and numeric account IDs out of the file, the records and the logs.
    accountLabel: z
      .string()
      .max(64)
      .regex(/^[a-z][a-z0-9]*(-[a-z0-9]+)*$/u)
      .refine((value) => !/\d{6,}/u.test(value), "must be a code name, not an account number"),
    enabled: z.boolean(),
    disabledReason: z.string().min(1).max(500).optional(),
    minIntervalSeconds: z
      .number()
      .int()
      .min(MIN_BINDING_INTERVAL_SECONDS)
      .max(MAX_INTERVAL_SECONDS)
      .default(DEFAULT_BINDING_LIMITS.minIntervalSeconds),
    // Optional counts: the last 60 minutes and the last 24 hours, like a Web account's.
    hourlyQuota: z.number().int().min(1).max(10_000).optional(),
    dailyQuota: z.number().int().min(1).max(100_000).optional(),
  })
  .strict()
  .superRefine((binding, context) => {
    if (!binding.enabled && binding.disabledReason === undefined)
      context.addIssue({
        code: "custom",
        path: ["disabledReason"],
        message: "is required when enabled is false",
      });
    if (binding.enabled && binding.disabledReason !== undefined)
      context.addIssue({
        code: "custom",
        path: ["disabledReason"],
        message: "is only allowed when enabled is false",
      });
  });

export const appBindingsSchema = z
  .object({
    version: z.literal(1),
    devices: z.array(deviceDeclarationSchema).min(1).max(16),
    // A package appears once, under its platform; bindings refer to it by platform code.
    platforms: z.partialRecord(platformCodeSchema, platformDeclarationSchema),
    bindings: z.array(bindingSchema).min(1).max(160),
  })
  .strict()
  .superRefine((config, context) => {
    const issue = (path: (string | number)[], message: string) =>
      context.addIssue({ code: "custom", path, message });

    const deviceIndex = new Map<string, number>();
    config.devices.forEach((device, index) => {
      const first = deviceIndex.get(device.id);
      if (first === undefined) deviceIndex.set(device.id, index);
      else issue(["devices", index, "id"], `duplicates devices.${first}.id`);
    });

    const packageOwner = new Map<string, string>();
    for (const [code, platform] of Object.entries(config.platforms)) {
      const owner = packageOwner.get(platform.package);
      if (owner === undefined) packageOwner.set(platform.package, code);
      else issue(["platforms", code, "package"], `duplicates platforms.${owner}.package`);
    }

    const pairIndex = new Map<string, number>();
    const labelOwner = new Map<string, { index: number; device: string }>();
    config.bindings.forEach((binding, index) => {
      if (!deviceIndex.has(binding.device))
        issue(["bindings", index, "device"], "is not a declared device");
      if (!Object.hasOwn(config.platforms, binding.platform))
        issue(["bindings", index, "platform"], "is not a declared platform");

      const pair = bindingKey(binding.device, binding.platform);
      const firstPair = pairIndex.get(pair);
      if (firstPair === undefined) pairIndex.set(pair, index);
      else
        issue(
          ["bindings", index, "platform"],
          `duplicates bindings.${firstPair}: one binding per device and platform`,
        );

      // Globally unique, which covers "one account never appears on two devices".
      const firstLabel = labelOwner.get(binding.accountLabel);
      if (firstLabel === undefined)
        labelOwner.set(binding.accountLabel, { index, device: binding.device });
      else
        issue(
          ["bindings", index, "accountLabel"],
          firstLabel.device === binding.device
            ? `duplicates bindings.${firstLabel.index}.accountLabel`
            : `duplicates bindings.${firstLabel.index}.accountLabel on another device`,
        );
    });
  });
export type AppBindings = z.infer<typeof appBindingsSchema>;
export type AppPlatformCode = AppBindings["bindings"][number]["platform"];

// Identity of a binding in logs, the ledger-derived counters and binding-status.json.
export function bindingKey(device: string, platform: string): string {
  return `${device}/${platform}`;
}

// One binding joined with its device declaration, package and both limit levels.
export interface ResolvedBinding {
  key: string;
  device: DeviceDeclaration;
  platformCode: AppPlatformCode;
  package: string;
  reviewedAppVersion: string;
  accountLabel: string;
  enabled: boolean;
  disabledReason?: string;
  // null: no such count.
  limits: { minIntervalSeconds: number; hourlyQuota: number | null; dailyQuota: number | null };
  platformLimits: { minIntervalSeconds: number; dailyCap: number | null };
}
export function resolveBindings(config: AppBindings): ResolvedBinding[] {
  return config.bindings.map((binding) => {
    // Both lookups are guaranteed by the schema's cross-reference rules.
    const device = config.devices.find((item) => item.id === binding.device)!;
    const platform = config.platforms[binding.platform]!;
    return {
      key: bindingKey(binding.device, binding.platform),
      device,
      platformCode: binding.platform,
      package: platform.package,
      reviewedAppVersion: binding.reviewedAppVersion,
      accountLabel: binding.accountLabel,
      enabled: binding.enabled,
      ...(binding.disabledReason === undefined ? {} : { disabledReason: binding.disabledReason }),
      limits: {
        minIntervalSeconds: binding.minIntervalSeconds,
        hourlyQuota: binding.hourlyQuota ?? null,
        dailyQuota: binding.dailyQuota ?? null,
      },
      platformLimits: {
        minIntervalSeconds: platform.minIntervalSeconds,
        dailyCap: platform.dailyCap ?? null,
      },
    };
  });
}

export function parseBindings(json: unknown): AppBindings {
  const parsed = appBindingsSchema.safeParse(json);
  if (!parsed.success) throw new AppSetupError("bindings_invalid", setupIssues(parsed.error));
  return parsed.data;
}

export const DEFAULT_BINDINGS_FILE = fileURLToPath(
  new URL("../../app-bindings.json", import.meta.url),
);
export function bindingsFilePath(env: { APP_COLLECTOR_BINDINGS_FILE?: string }): string {
  return env.APP_COLLECTOR_BINDINGS_FILE ?? DEFAULT_BINDINGS_FILE;
}

const MAX_BINDINGS_FILE_BYTES = 256 * 1024;
const fileIssue = (message: string) =>
  new AppSetupError("bindings_invalid", [{ path: "", message }]);

export interface LoadedBindings {
  path: string;
  // Hash of the file as read; recorded in batch.json so a batch names the configuration it used.
  sha256: string;
  config: AppBindings;
}
export async function loadBindingsFile(path: string): Promise<LoadedBindings> {
  if (!isAbsolute(path)) throw fileIssue("bindings file must be an absolute path");
  let raw: string;
  try {
    const stats = await lstat(path);
    if (!stats.isFile() || stats.isSymbolicLink())
      throw fileIssue("bindings file must be a regular file, not a symlink");
    if (stats.size > MAX_BINDINGS_FILE_BYTES) throw fileIssue("bindings file is too large");
    raw = await readFile(path, "utf8");
  } catch (error) {
    if (error instanceof AppSetupError) throw error;
    throw fileIssue("bindings file cannot be read");
  }
  let json: unknown;
  try {
    json = JSON.parse(raw);
  } catch {
    throw fileIssue("bindings file is not valid JSON");
  }
  return {
    path,
    sha256: createHash("sha256").update(raw).digest("hex"),
    config: parseBindings(json),
  };
}
