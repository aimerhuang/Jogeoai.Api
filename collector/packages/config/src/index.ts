import { isAbsolute } from "node:path";
import { platformCodeSchema, regionSchema } from "@aivc/contracts";
import { z } from "zod";

const databaseUrlSchema = z
  .url()
  .refine((value) => value.startsWith("postgres://") || value.startsWith("postgresql://"), {
    message: "DATABASE_URL must use postgres:// or postgresql://",
  });

export function loadDatabaseConfig(env: NodeJS.ProcessEnv = process.env) {
  return z.object({ DATABASE_URL: databaseUrlSchema }).parse(env);
}

export function loadApiConfig(env: NodeJS.ProcessEnv = process.env) {
  return z
    .object({
      DATABASE_URL: databaseUrlSchema,
      API_HOST: z.string().default("0.0.0.0"),
      API_PORT: z.coerce.number().int().min(1).max(65535).default(4311),
      COLLECTOR_CN_SERVICE_TOKEN: z.string().min(24),
      COLLECTOR_OVERSEAS_SERVICE_TOKEN: z.string().min(24),
    })
    .refine(
      (value) => value.COLLECTOR_CN_SERVICE_TOKEN !== value.COLLECTOR_OVERSEAS_SERVICE_TOKEN,
      {
        message: "Collector service tokens must be distinct for each region",
        path: ["COLLECTOR_OVERSEAS_SERVICE_TOKEN"],
      },
    )
    .parse(env);
}

export function loadWorkerConfig(env: NodeJS.ProcessEnv = process.env) {
  return z
    .object({
      DATABASE_URL: databaseUrlSchema,
      SCHEDULE_DATE: z.string().date().optional(),
      WORKER_INTERVAL_MS: z.coerce.number().int().min(1_000).default(86_400_000),
      WORKER_RUN_ONCE: z.enum(["true", "false"]).default("false"),
    })
    .parse(env);
}

const sessionProfileIdSchema = z.uuid().optional();
const notifyCommandSchema = z
  .string()
  .min(1)
  .refine(isAbsolute, "COLLECTOR_NOTIFY_COMMAND must be an absolute executable path")
  .optional();
const collectorTokenSchema = z.string().min(24);
const collectorHttpTimeoutSchema = z.coerce.number().int().min(100).max(30_000).default(10_000);

function collectorApiUrlIssues(value: string): string[] {
  const issues: string[] = [];
  const url = new URL(value);
  if (
    !["http:", "https:"].includes(url.protocol) ||
    url.username ||
    url.password ||
    url.search ||
    url.hash
  ) {
    issues.push("Collector API must be an HTTP(S) URL without credentials, query or fragment");
  }
  if (
    url.protocol === "http:" &&
    !["localhost", "127.0.0.1", "[::1]", "api"].includes(url.hostname)
  ) {
    issues.push("Remote Collector API requires HTTPS");
  }
  return issues;
}

export function loadCollectorConfig(env: NodeJS.ProcessEnv = process.env) {
  return z
    .object({
      COLLECTOR_API_URL: z.url(),
      COLLECTOR_SERVICE_TOKEN: collectorTokenSchema,
      COLLECTOR_TENANT_ID: z.uuid(),
      COLLECTOR_NODE_ID: z.string().min(1).max(128),
      COLLECTOR_REGION: regionSchema,
      COLLECTOR_BUILD_REVISION: z.string().min(1).default("local-dev"),
      COLLECTOR_EGRESS_COUNTRY: z
        .string()
        .length(2)
        .transform((value) => value.toUpperCase()),
      COLLECTOR_SPOOL_DIRECTORY: z.string().min(1).default(".collector-spool"),
      COLLECTOR_SPOOL_KEY: z
        .string()
        .transform((value) => Buffer.from(value, "base64"))
        .refine((value) => value.length === 32, "COLLECTOR_SPOOL_KEY must decode to 32 bytes"),
      COLLECTOR_SUPPORTED_PLATFORMS: z
        .string()
        .transform((value) => value.split(",").filter(Boolean))
        .pipe(z.array(platformCodeSchema).min(1)),
      COLLECTOR_CAPACITY: z.coerce.number().int().min(1).max(50).default(5),
      COLLECTOR_LEASE_SECONDS: z.coerce.number().int().min(5).max(900).default(60),
      COLLECTOR_POLL_INTERVAL_MS: z.coerce.number().int().min(100).default(1_000),
      COLLECTOR_RUN_ONCE: z.enum(["true", "false"]).default("false"),
      COLLECTOR_MODE: z.enum(["mock", "real"]).default("mock"),
      COLLECTOR_HTTP_TIMEOUT_MS: collectorHttpTimeoutSchema,
      COLLECTOR_HEARTBEAT_INTERVAL_MS: z.coerce.number().int().min(100).default(10_000),
      COLLECTOR_COLLECTION_TIMEOUT_MS: z.coerce
        .number()
        .int()
        .min(1_000)
        .max(900_000)
        .default(180_000),
      COLLECTOR_MIN_REQUEST_INTERVAL_MS: z.coerce.number().int().min(0).default(15_000),
      COLLECTOR_MAX_TASKS_PER_RUN: z.coerce.number().int().min(1).max(10).default(4),
      COLLECTOR_EXPECTED_PROJECT_ID: z.uuid().optional(),
      COLLECTOR_RETRY_TASK_ID: z.uuid().optional(),
      // Operator-approved question list (0600 JSON file). Unset keeps the built-in list.
      COLLECTOR_APPROVED_QUESTIONS_FILE: z
        .string()
        .min(1)
        .refine(isAbsolute, "COLLECTOR_APPROVED_QUESTIONS_FILE must be an absolute path")
        .optional(),
      // Registered account (control-plane session profile) this process works for. Optional;
      // with it real mode runs resident and the control plane decides when the account works.
      COLLECTOR_SESSION_PROFILE_ID: sessionProfileIdSchema,
      // How long a visible challenge may wait for the operator before the task is released.
      // 0 disables waiting: a challenge fails the task immediately.
      COLLECTOR_CHALLENGE_WAIT_MS: z.coerce.number().int().min(0).max(1_800_000).default(600_000),
      // Optional executable receiving operator notifications as JSON on stdin (no shell).
      COLLECTOR_NOTIFY_COMMAND: notifyCommandSchema,
    })
    .superRefine((value, context) => {
      const issue = (message: string) => context.addIssue({ code: "custom", message });
      collectorApiUrlIssues(value.COLLECTOR_API_URL).forEach(issue);
      if ((value.COLLECTOR_REGION === "cn") !== (value.COLLECTOR_EGRESS_COUNTRY === "CN")) {
        issue("EGRESS_REGION_MISMATCH");
      }
      if (value.COLLECTOR_MODE !== "real") return;
      const platform = value.COLLECTOR_SUPPORTED_PLATFORMS[0];
      const isCnPlatform = platform !== "chatgpt" && platform !== "claude" && platform !== "gemini";
      if (
        value.COLLECTOR_CAPACITY !== 1 ||
        value.COLLECTOR_SUPPORTED_PLATFORMS.length !== 1 ||
        (isCnPlatform && value.COLLECTOR_REGION !== "cn") ||
        (!isCnPlatform && value.COLLECTOR_REGION !== "overseas")
      ) {
        issue(
          "Real mode requires capacity=1, a region matching the supported platform and exactly one consumer-web platform",
        );
      }
      if (!value.COLLECTOR_EXPECTED_PROJECT_ID)
        issue("Real mode requires COLLECTOR_EXPECTED_PROJECT_ID");
      if (value.COLLECTOR_HEARTBEAT_INTERVAL_MS * 2 >= value.COLLECTOR_LEASE_SECONDS * 1_000) {
        issue("Heartbeat interval must be less than half the lease duration");
      }
      if (value.COLLECTOR_MIN_REQUEST_INTERVAL_MS < 1_000)
        issue("Real mode minimum request interval is 1000ms");
      if (value.COLLECTOR_HTTP_TIMEOUT_MS >= value.COLLECTOR_LEASE_SECONDS * 500)
        issue("HTTP timeout must be less than half the lease duration");
    })
    .parse(env);
}

export type CollectorConfig = ReturnType<typeof loadCollectorConfig>;

export function loadSessionConfig(env: NodeJS.ProcessEnv = process.env) {
  if (env.DEBUG || env.PWDEBUG || env.PLAYWRIGHT_TRACE_DIR) {
    throw new Error("Browser debug/trace environment must be disabled for session safety");
  }
  return z
    .object({
      COLLECTOR_PROFILE_DIRECTORY: z.string().min(1),
      COLLECTOR_BROWSER_CDP_URL: z
        .url()
        .refine((value) => {
          const url = new URL(value);
          return (
            ["http:", "https:"].includes(url.protocol) &&
            ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname)
          );
        }, "Browser CDP endpoint must be local")
        .optional(),
      COLLECTOR_BROWSER_HEADLESS: z.enum(["true", "false"]).default("false"),
      COLLECTOR_SUPPORTED_PLATFORMS: z
        .enum([
          "doubao",
          "ernie",
          "zhipu_qingyan",
          "qwen",
          "kimi",
          "deepseek",
          "tencent_yuanbao",
          "chatgpt",
          "gemini",
        ])
        .default("doubao"),
      DOUBAO_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      ERNIE_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      QINGYAN_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      QWEN_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      KIMI_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      DEEPSEEK_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      YUANBAO_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      CHATGPT_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      GEMINI_PAGE_CONTRACT_PATH: z.string().min(1).optional(),
      // Account identity for `probe`: a ready probe reports `verified` to the control plane.
      // Login never needs these; they are only checked by `requireSessionReporting`.
      COLLECTOR_SESSION_PROFILE_ID: sessionProfileIdSchema,
      COLLECTOR_API_URL: z.url().optional(),
      COLLECTOR_SERVICE_TOKEN: collectorTokenSchema.optional(),
      COLLECTOR_TENANT_ID: z.uuid().optional(),
      COLLECTOR_NODE_ID: z.string().min(1).max(128).optional(),
      COLLECTOR_REGION: regionSchema.optional(),
      COLLECTOR_HTTP_TIMEOUT_MS: collectorHttpTimeoutSchema,
    })
    .superRefine((value, context) => {
      if (value.COLLECTOR_API_URL)
        collectorApiUrlIssues(value.COLLECTOR_API_URL).forEach((message) =>
          context.addIssue({ code: "custom", message }),
        );
    })
    .parse(env);
}

export type SessionConfig = ReturnType<typeof loadSessionConfig>;

export interface SessionReportingConfig {
  COLLECTOR_SESSION_PROFILE_ID: string;
  COLLECTOR_API_URL: string;
  COLLECTOR_SERVICE_TOKEN: string;
  COLLECTOR_TENANT_ID: string;
  COLLECTOR_NODE_ID: string;
  COLLECTOR_REGION: z.infer<typeof regionSchema>;
  COLLECTOR_HTTP_TIMEOUT_MS: number;
}

// Undefined when no session profile is configured (legacy single-account probe). With a profile,
// every field needed to report `verified` is required, so a probe never silently skips it.
export function requireSessionReporting(config: SessionConfig): SessionReportingConfig | undefined {
  const profileId = config.COLLECTOR_SESSION_PROFILE_ID;
  if (!profileId) return undefined;
  const {
    COLLECTOR_API_URL: apiUrl,
    COLLECTOR_SERVICE_TOKEN: token,
    COLLECTOR_TENANT_ID: tenantId,
    COLLECTOR_NODE_ID: nodeId,
    COLLECTOR_REGION: region,
  } = config;
  if (!apiUrl || !token || !tenantId || !nodeId || !region) {
    throw new Error(
      "COLLECTOR_SESSION_PROFILE_ID requires COLLECTOR_API_URL, COLLECTOR_SERVICE_TOKEN, COLLECTOR_TENANT_ID, COLLECTOR_NODE_ID and COLLECTOR_REGION",
    );
  }
  return {
    COLLECTOR_SESSION_PROFILE_ID: profileId,
    COLLECTOR_API_URL: apiUrl,
    COLLECTOR_SERVICE_TOKEN: token,
    COLLECTOR_TENANT_ID: tenantId,
    COLLECTOR_NODE_ID: nodeId,
    COLLECTOR_REGION: region,
    COLLECTOR_HTTP_TIMEOUT_MS: config.COLLECTOR_HTTP_TIMEOUT_MS,
  };
}
