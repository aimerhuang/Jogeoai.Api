import { parseArgs } from "node:util";

import { loadDatabaseConfig } from "@aivc/config";
import { platformCodeSchema, regionSchema } from "@aivc/contracts";
import { DomainError } from "@aivc/domain";

import {
  FIXTURE_IDS,
  configurePlatformDispatch,
  createDatabase,
  currentShanghaiDate,
  generateDailyTasks,
  listPlatformDispatchStates,
  listSessionProfileMetrics,
  migrateDatabase,
  registerSessionProfile,
  resumePlatformDispatch,
  seedFixture,
  setSessionProfileStatus,
  type Database,
} from "./index.ts";

const USAGE = [
  "Usage: cli.ts <migrate|seed|schedule>",
  "       cli.ts profiles add --platform <code> --region <cn|overseas> --label <alias> [--hourly <n>] [--daily <n>] [--min-interval <seconds>]",
  "       cli.ts profiles set-status <sessionProfileId> <active|disabled>",
  "       cli.ts profiles list [--platform <code>] [--region <cn|overseas>]",
  "       cli.ts platforms set --platform <code> --region <cn|overseas> [--min-interval <seconds>] [--daily-cap <n>] [--breaker-threshold <n>] [--breaker-window <minutes>] [--retry-backoff <seconds>] [--max-failures <n>] [--cooldown-base <seconds>] [--cooldown-max <seconds>] [--probe-offline <minutes 1-1440>]",
  "       cli.ts platforms resume --platform <code> --region <cn|overseas>",
  "       cli.ts platforms list",
  "Operator commands are tenant-scoped: pass --tenant <uuid> or set AIVC_TENANT_ID.",
].join("\n");

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;

class UsageError extends Error {}

type Flags = Record<string, string | undefined>;

function parseFlags(args: string[], allowed: string[]): { flags: Flags; positionals: string[] } {
  const options = Object.fromEntries(
    ["tenant", ...allowed].map((name) => [name, { type: "string" as const }]),
  );
  try {
    const parsed = parseArgs({ args, options, allowPositionals: true, strict: true });
    return { flags: parsed.values as Flags, positionals: parsed.positionals };
  } catch {
    throw new UsageError("Unknown or malformed option");
  }
}

function tenantFrom(flags: Flags): string {
  const tenantId = flags.tenant ?? process.env.AIVC_TENANT_ID;
  if (!tenantId || !UUID.test(tenantId)) {
    throw new UsageError("A tenant UUID is required (--tenant or AIVC_TENANT_ID)");
  }
  return tenantId;
}

function required(flags: Flags, name: string): string {
  const value = flags[name];
  if (!value) throw new UsageError(`--${name} is required`);
  return value;
}

function platformFrom(value: string): string {
  const parsed = platformCodeSchema.safeParse(value);
  if (!parsed.success) throw new UsageError("Unknown platform code");
  return parsed.data;
}

function regionFrom(value: string) {
  const parsed = regionSchema.safeParse(value);
  if (!parsed.success) throw new UsageError("Region must be cn or overseas");
  return parsed.data;
}

function integerFlag(
  flags: Flags,
  name: string,
  minimum: number,
  maximum?: number,
): number | undefined {
  const value = flags[name];
  if (value === undefined) return undefined;
  if (!/^\d{1,9}$/u.test(value) || Number(value) < minimum) {
    throw new UsageError(`--${name} must be an integer >= ${minimum}`);
  }
  if (maximum !== undefined && Number(value) > maximum) {
    throw new UsageError(`--${name} must be an integer between ${minimum} and ${maximum}`);
  }
  return Number(value);
}

function print(result: object): void {
  console.log(JSON.stringify(result));
}

async function profilesCommand(database: Database, args: string[]): Promise<void> {
  const [action, ...rest] = args;
  const command = `profiles ${action ?? ""}`.trim();
  switch (action) {
    case "add": {
      const { flags } = parseFlags(rest, [
        "platform",
        "region",
        "label",
        "hourly",
        "daily",
        "min-interval",
      ]);
      const label = required(flags, "label").trim();
      if (label.length === 0 || label.length > 128) {
        throw new UsageError("--label must be 1-128 characters");
      }
      const sessionProfileId = await registerSessionProfile(database, {
        tenantId: tenantFrom(flags),
        platformCode: platformFrom(required(flags, "platform")),
        region: regionFrom(required(flags, "region")),
        label,
        hourlyQuota: integerFlag(flags, "hourly", 1),
        dailyQuota: integerFlag(flags, "daily", 1),
        minIntervalSeconds: integerFlag(flags, "min-interval", 0),
      });
      print({ command, ok: true, sessionProfileId });
      return;
    }
    case "set-status": {
      const { flags, positionals } = parseFlags(rest, []);
      const [sessionProfileId, status, ...extra] = positionals;
      if (!sessionProfileId || !UUID.test(sessionProfileId) || extra.length > 0) {
        throw new UsageError("set-status takes <sessionProfileId> <active|disabled>");
      }
      if (status !== "active" && status !== "disabled") {
        throw new UsageError("Operators may only set status active or disabled");
      }
      await setSessionProfileStatus(database, {
        tenantId: tenantFrom(flags),
        sessionProfileId,
        status,
      });
      print({ command, ok: true, sessionProfileId, status });
      return;
    }
    case "list": {
      const { flags } = parseFlags(rest, ["platform", "region"]);
      const profiles = await listSessionProfileMetrics(database, {
        tenantId: tenantFrom(flags),
        platformCode: flags.platform === undefined ? undefined : platformFrom(flags.platform),
        region: flags.region === undefined ? undefined : regionFrom(flags.region),
      });
      print({ command, ok: true, profiles });
      return;
    }
    default:
      throw new UsageError("Unknown profiles action");
  }
}

async function platformsCommand(database: Database, args: string[]): Promise<void> {
  const [action, ...rest] = args;
  const command = `platforms ${action ?? ""}`.trim();
  switch (action) {
    case "set": {
      const { flags } = parseFlags(rest, [
        "platform",
        "region",
        "min-interval",
        "daily-cap",
        "breaker-threshold",
        "breaker-window",
        "retry-backoff",
        "max-failures",
        "cooldown-base",
        "cooldown-max",
        "probe-offline",
      ]);
      const settings = {
        minIntervalSeconds: integerFlag(flags, "min-interval", 0),
        dailyCap: integerFlag(flags, "daily-cap", 1),
        breakerProfileThreshold: integerFlag(flags, "breaker-threshold", 1),
        breakerWindowMinutes: integerFlag(flags, "breaker-window", 1),
        retryBackoffSeconds: integerFlag(flags, "retry-backoff", 0),
        maxTaskFailures: integerFlag(flags, "max-failures", 1),
        rateLimitCooldownBaseSeconds: integerFlag(flags, "cooldown-base", 1),
        rateLimitCooldownMaxSeconds: integerFlag(flags, "cooldown-max", 1),
        probeFailureOfflineMinutes: integerFlag(flags, "probe-offline", 1, 1440),
      };
      if (Object.values(settings).every((value) => value === undefined)) {
        throw new UsageError("platforms set needs at least one setting");
      }
      const tenantId = tenantFrom(flags);
      const platformCode = platformFrom(required(flags, "platform"));
      const region = regionFrom(required(flags, "region"));
      await configurePlatformDispatch(database, { tenantId, platformCode, region, ...settings });
      const platform = (await listPlatformDispatchStates(database, { tenantId })).find(
        (state) => state.platformCode === platformCode && state.region === region,
      );
      print({ command, ok: true, platform });
      return;
    }
    case "resume": {
      const { flags } = parseFlags(rest, ["platform", "region"]);
      const resumed = await resumePlatformDispatch(database, {
        tenantId: tenantFrom(flags),
        platformCode: platformFrom(required(flags, "platform")),
        region: regionFrom(required(flags, "region")),
      });
      print({ command, ok: true, resumed });
      return;
    }
    case "list": {
      const { flags } = parseFlags(rest, []);
      const platforms = await listPlatformDispatchStates(database, {
        tenantId: tenantFrom(flags),
      });
      print({ command, ok: true, platforms });
      return;
    }
    default:
      throw new UsageError("Unknown platforms action");
  }
}

// Errors carry only codes and fixed messages: no SQL detail, parameters or row values.
function reportError(command: string | undefined, error: unknown): void {
  if (error instanceof UsageError) {
    console.error(JSON.stringify({ command, ok: false, code: "USAGE", message: error.message }));
    console.error(USAGE);
    process.exitCode = 2;
    return;
  }
  if (error instanceof DomainError) {
    console.error(JSON.stringify({ command, ok: false, code: error.code, message: error.message }));
  } else {
    const postgresError = error as { code?: unknown; constraint_name?: unknown };
    console.error(
      JSON.stringify({
        command,
        ok: false,
        code: "CLI_FAILED",
        ...(typeof postgresError.code === "string" ? { sqlState: postgresError.code } : {}),
        ...(typeof postgresError.constraint_name === "string"
          ? { constraint: postgresError.constraint_name }
          : {}),
      }),
    );
  }
  process.exitCode = 1;
}

const [command, ...args] = process.argv.slice(2);
const config = loadDatabaseConfig();
const database = createDatabase(config.DATABASE_URL, 1);

try {
  switch (command) {
    case "migrate":
      await migrateDatabase(database);
      print({ command, ok: true });
      break;
    case "seed":
      await migrateDatabase(database);
      await seedFixture(database);
      print({ command, ok: true, tenantId: FIXTURE_IDS.tenantId });
      break;
    case "schedule": {
      await migrateDatabase(database);
      await seedFixture(database);
      const scheduledDate = process.env.SCHEDULE_DATE ?? currentShanghaiDate();
      const result = await generateDailyTasks(database, {
        tenantId: FIXTURE_IDS.tenantId,
        brandProjectId: FIXTURE_IDS.brandProjectId,
        scheduledDate,
      });
      print({ command, scheduledDate, ...result });
      break;
    }
    case "profiles":
      await profilesCommand(database, args);
      break;
    case "platforms":
      await platformsCommand(database, args);
      break;
    default:
      throw new UsageError("Unknown command");
  }
} catch (error) {
  // Bootstrap commands keep their previous behaviour (full error, non-zero exit).
  if (!(error instanceof UsageError) && command !== "profiles" && command !== "platforms") {
    throw error;
  }
  reportError(command, error);
} finally {
  await database.end();
}
