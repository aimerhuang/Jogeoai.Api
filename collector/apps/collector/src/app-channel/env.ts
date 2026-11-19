import { isAbsolute, resolve } from "node:path";
import { z } from "zod";
import { controlPlaneUrlProblem } from "./control-plane.ts";
import { AppSetupError, setupIssues } from "./failure.ts";

const absolutePath = z
  .string()
  .min(1)
  .max(1024)
  .refine(isAbsolute, "must be an absolute path")
  .transform((value) => resolve(value));

// Process environment of the App-channel script (design §11). The only secret is
// APP_COLLECTOR_NODE_TOKEN; validation errors name the variable, never its value.
export const appChannelEnvSchema = z.object({
  // Defaults to the committed apps/collector/app-bindings.json.
  APP_COLLECTOR_BINDINGS_FILE: absolutePath.optional(),
  // Required by commands that write evidence; `check` does not use it.
  APP_COLLECTOR_EVIDENCE_DIR: absolutePath.optional(),
  // Private directory with one operator-reviewed <platformCode>.json per platform. Required by
  // `run`; with it `check` also opens each App and looks whether its account is logged in.
  APP_COLLECTOR_CONTRACT_DIR: absolutePath.optional(),
  // Upper bound from the send tap to the App's completion marker.
  APP_COLLECTOR_COLLECTION_TIMEOUT_SECONDS: z.coerce.number().int().min(30).max(900).default(300),
  // `prune` deletes videos older than this; `--days` overrides it.
  APP_COLLECTOR_VIDEO_RETENTION_DAYS: z.coerce.number().int().min(1).max(3_650).default(30),
  // Written into every record; default: the repository's commit (with "-dirty" when modified).
  APP_COLLECTOR_SCRIPT_REVISION: z
    .string()
    .regex(/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/u)
    .optional(),
  // Loopback port of the script's own Appium server; default: a free port.
  APP_COLLECTOR_APPIUM_PORT: z.coerce.number().int().min(1_024).max(65_535).optional(),
  // Debugging only: file for Appium's own error-level log. Keep it in a private directory.
  APP_COLLECTOR_APPIUM_LOG_FILE: absolutePath.optional(),
  // The Web channel's approved-question file and its tenant/project binding. Unset file: the
  // built-in smoke list, as on the Web channel.
  COLLECTOR_APPROVED_QUESTIONS_FILE: absolutePath.optional(),
  COLLECTOR_TENANT_ID: z.uuid().optional(),
  COLLECTOR_EXPECTED_PROJECT_ID: z.uuid().optional(),
  // Default: `adb` / `ffprobe` from PATH. `ffprobe` is used only with recording switched on.
  APP_COLLECTOR_ADB_PATH: absolutePath.optional(),
  APP_COLLECTOR_FFPROBE_PATH: absolutePath.optional(),
  // A batch does not start below this much free space in the evidence directory.
  APP_COLLECTOR_MIN_FREE_GB: z.coerce.number().min(1).max(10_000).default(10),
  // Screen recording of every attempt (PRD FR-A8). Off by default: no recorder is started, the
  // record has no video and a valid sample needs none. The size and bit rate below apply to `on`.
  APP_COLLECTOR_RECORD_SCREEN: z.enum(["on", "off"]).default("off"),
  APP_COLLECTOR_RECORD_SIZE: z
    .string()
    .regex(/^[1-9]\d{2,3}x[1-9]\d{2,3}$/u)
    .default("720x1600"),
  // Control-plane mode (`serve`, task 11-16-app-collector-control-plane). All three are required
  // by `serve` and unused otherwise. The URL is the Jogeoai control plane (never 4311 or 4312);
  // the node ID and its token come from the operator's node registration.
  APP_COLLECTOR_API_URL: z
    .string()
    .max(2048)
    .superRefine((value, context) => {
      const problem = controlPlaneUrlProblem(value);
      if (problem) context.addIssue({ code: "custom", message: problem });
    })
    .optional(),
  APP_COLLECTOR_NODE_ID: z
    .string()
    .regex(/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/u)
    .optional(),
  APP_COLLECTOR_NODE_TOKEN: z.string().min(16).max(512).optional(),
  // Lease length asked for and renewed by heartbeats every third of it.
  APP_COLLECTOR_LEASE_SECONDS: z.coerce.number().int().min(60).max(900).default(180),
  APP_COLLECTOR_HTTP_TIMEOUT_MS: z.coerce.number().int().min(1_000).max(120_000).default(15_000),
  APP_COLLECTOR_RECORD_BIT_RATE: z.coerce
    .number()
    .int()
    .min(100_000)
    .max(20_000_000)
    .default(2_000_000),
});
export type AppChannelEnv = z.infer<typeof appChannelEnvSchema>;

export function loadAppChannelEnv(env: NodeJS.ProcessEnv = process.env): AppChannelEnv {
  // Only the known keys are read; an empty value means "not set".
  const known = Object.fromEntries(
    Object.keys(appChannelEnvSchema.shape)
      .map((key) => [key, env[key]] as const)
      .filter(([, value]) => value !== undefined && value !== ""),
  );
  const parsed = appChannelEnvSchema.safeParse(known);
  if (!parsed.success) throw new AppSetupError("environment_invalid", setupIssues(parsed.error));
  return parsed.data;
}
