import { describe, expect, it } from "vitest";

import {
  loadApiConfig,
  loadCollectorConfig,
  loadSessionConfig,
  requireSessionReporting,
} from "./index.ts";

const baseEnv = {
  DATABASE_URL: "postgres://user:password@localhost:5432/database",
  COLLECTOR_CN_SERVICE_TOKEN: "cn-test-token-with-at-least-24-characters",
  COLLECTOR_OVERSEAS_SERVICE_TOKEN: "overseas-test-token-with-at-least-24-characters",
};

describe("API configuration", () => {
  it("requires distinct collector credentials for each region", () => {
    expect(loadApiConfig(baseEnv)).toMatchObject(baseEnv);
    expect(() =>
      loadApiConfig({
        ...baseEnv,
        COLLECTOR_OVERSEAS_SERVICE_TOKEN: baseEnv.COLLECTOR_CN_SERVICE_TOKEN,
      }),
    ).toThrowError(/Collector service tokens must be distinct/);
  });
});

const collectorEnv = {
  COLLECTOR_API_URL: "http://127.0.0.1:4314",
  COLLECTOR_SERVICE_TOKEN: "test-only-collector-token-123456",
  COLLECTOR_TENANT_ID: "11111111-1111-4111-8111-111111111111",
  COLLECTOR_NODE_ID: "node",
  COLLECTOR_REGION: "cn",
  COLLECTOR_EGRESS_COUNTRY: "CN",
  COLLECTOR_SUPPORTED_PLATFORMS: "doubao",
  COLLECTOR_SPOOL_KEY: Buffer.alloc(32, 1).toString("base64"),
  COLLECTOR_CAPACITY: "1",
  COLLECTOR_EXPECTED_PROJECT_ID: "22222222-2222-4222-8222-222222222222",
  COLLECTOR_SESSION_PROFILE_ID: "66666666-6666-4666-8666-666666666666",
};
describe("Collector real mode safety", () => {
  it("is default-off and supports profile-only login config", () => {
    expect(loadCollectorConfig(collectorEnv).COLLECTOR_MODE).toBe("mock");
    expect(
      loadSessionConfig({ COLLECTOR_PROFILE_DIRECTORY: "/aivc/session" })
        .COLLECTOR_BROWSER_HEADLESS,
    ).toBe("false");
  });
  it("refuses verbose browser diagnostics that could record prompt/session content", () => {
    expect(() =>
      loadSessionConfig({ COLLECTOR_PROFILE_DIRECTORY: "/aivc/session", DEBUG: "pw:*" }),
    ).toThrow();
    expect(() =>
      loadSessionConfig({ COLLECTOR_PROFILE_DIRECTORY: "/aivc/session", PWDEBUG: "1" }),
    ).toThrow();
  });
  it.each([
    { COLLECTOR_SUPPORTED_PLATFORMS: "doubao,ernie" },
    { COLLECTOR_SUPPORTED_PLATFORMS: "ernie,ernie" },
    { COLLECTOR_SUPPORTED_PLATFORMS: "zhipu_qingyan,ernie" },
    { COLLECTOR_SUPPORTED_PLATFORMS: "zhipu_qingyan,zhipu_qingyan" },
    { COLLECTOR_SUPPORTED_PLATFORMS: "qwen,ernie" },
    { COLLECTOR_SUPPORTED_PLATFORMS: "qwen,qwen" },
    { COLLECTOR_SUPPORTED_PLATFORMS: "kimi,kimi" },
    { COLLECTOR_SUPPORTED_PLATFORMS: "doubao,kimi" },
    { COLLECTOR_SUPPORTED_PLATFORMS: "tencent_yuanbao,tencent_yuanbao" },
    { COLLECTOR_CAPACITY: "2" },
    { COLLECTOR_MAX_TASKS_PER_RUN: "11" },
    { COLLECTOR_MAX_TASKS_PER_RUN: "0" },
    { COLLECTOR_APPROVED_QUESTIONS_FILE: "relative/approved-questions.json" },
    { COLLECTOR_EXPECTED_PROJECT_ID: undefined },
    { COLLECTOR_SESSION_PROFILE_ID: "account-1" },
    { COLLECTOR_CHALLENGE_WAIT_MS: "1800001" },
    { COLLECTOR_CHALLENGE_WAIT_MS: "-1" },
    { COLLECTOR_NOTIFY_COMMAND: "notify.sh" },
    { COLLECTOR_HEARTBEAT_INTERVAL_MS: "30000" },
    { COLLECTOR_EGRESS_COUNTRY: "US" },
    { COLLECTOR_API_URL: "https://user:password@example.com" },
    { COLLECTOR_API_URL: "http://remote.example.com" },
    { COLLECTOR_MIN_REQUEST_INTERVAL_MS: "0" },
  ])("rejects an unsafe real-mode combination %s", (patch) => {
    expect(() =>
      loadCollectorConfig({ ...collectorEnv, COLLECTOR_MODE: "real", ...patch }),
    ).toThrow();
  });
  it("keeps a four-task default and accepts a configured cap of up to ten", () => {
    expect(
      loadCollectorConfig({ ...collectorEnv, COLLECTOR_MODE: "real" }).COLLECTOR_MAX_TASKS_PER_RUN,
    ).toBe(4);
    expect(
      loadCollectorConfig({
        ...collectorEnv,
        COLLECTOR_MODE: "real",
        COLLECTOR_MAX_TASKS_PER_RUN: "10",
      }).COLLECTOR_MAX_TASKS_PER_RUN,
    ).toBe(10);
  });
  it("accepts an optional absolute approved-question file path", () => {
    expect(
      loadCollectorConfig({ ...collectorEnv, COLLECTOR_MODE: "real" })
        .COLLECTOR_APPROVED_QUESTIONS_FILE,
    ).toBeUndefined();
    expect(
      loadCollectorConfig({
        ...collectorEnv,
        COLLECTOR_MODE: "real",
        COLLECTOR_APPROVED_QUESTIONS_FILE: "/aivc/approved-questions.json",
      }).COLLECTOR_APPROVED_QUESTIONS_FILE,
    ).toBe("/aivc/approved-questions.json");
  });
  it.each(["doubao", "ernie", "zhipu_qingyan", "qwen", "kimi", "deepseek", "tencent_yuanbao"])(
    "allows selected real platform %s and profile-only config",
    (platform) => {
      expect(
        loadCollectorConfig({
          ...collectorEnv,
          COLLECTOR_MODE: "real",
          COLLECTOR_SUPPORTED_PLATFORMS: platform,
        }).COLLECTOR_SUPPORTED_PLATFORMS,
      ).toEqual([platform]);
      expect(
        loadSessionConfig({
          COLLECTOR_PROFILE_DIRECTORY: "/aivc/ernie",
          COLLECTOR_SUPPORTED_PLATFORMS: platform,
        }).COLLECTOR_SUPPORTED_PLATFORMS,
      ).toBe(platform);
      expect(() =>
        loadSessionConfig({
          COLLECTOR_PROFILE_DIRECTORY: "/aivc/ernie",
          COLLECTOR_SUPPORTED_PLATFORMS: "ernie,doubao",
        }),
      ).toThrow();
    },
  );
  it.each(["chatgpt", "gemini"])(
    "allows overseas platform %s only with overseas region and egress",
    (platform) => {
      expect(
        loadCollectorConfig({
          ...collectorEnv,
          COLLECTOR_MODE: "real",
          COLLECTOR_REGION: "overseas",
          COLLECTOR_EGRESS_COUNTRY: "JP",
          COLLECTOR_SUPPORTED_PLATFORMS: platform,
        }).COLLECTOR_SUPPORTED_PLATFORMS,
      ).toEqual([platform]);
      expect(() =>
        loadCollectorConfig({
          ...collectorEnv,
          COLLECTOR_MODE: "real",
          COLLECTOR_SUPPORTED_PLATFORMS: platform,
        }),
      ).toThrow();
    },
  );
  it("accepts a local CDP endpoint for a user-managed browser", () => {
    expect(
      loadSessionConfig({
        COLLECTOR_PROFILE_DIRECTORY: "/aivc/chatgpt-cdp",
        COLLECTOR_BROWSER_CDP_URL: "http://127.0.0.1:9222",
        COLLECTOR_SUPPORTED_PLATFORMS: "chatgpt",
      }).COLLECTOR_BROWSER_CDP_URL,
    ).toBe("http://127.0.0.1:9222");

    expect(
      loadSessionConfig({
        COLLECTOR_PROFILE_DIRECTORY: "/aivc/gemini",
        COLLECTOR_SUPPORTED_PLATFORMS: "gemini",
        GEMINI_PAGE_CONTRACT_PATH: "/aivc/gemini-contract.json",
      }),
    ).toMatchObject({
      COLLECTOR_SUPPORTED_PLATFORMS: "gemini",
      GEMINI_PAGE_CONTRACT_PATH: "/aivc/gemini-contract.json",
    });
  });
});

describe("account pool and challenge wait configuration", () => {
  it("defaults to a ten-minute challenge wait, accepts 0 (no wait) and an absolute notify command", () => {
    const config = loadCollectorConfig({ ...collectorEnv, COLLECTOR_MODE: "real" });
    expect(config.COLLECTOR_SESSION_PROFILE_ID).toBe(collectorEnv.COLLECTOR_SESSION_PROFILE_ID);
    expect(config.COLLECTOR_CHALLENGE_WAIT_MS).toBe(600_000);
    expect(config.COLLECTOR_NOTIFY_COMMAND).toBeUndefined();
    expect(
      loadCollectorConfig({
        ...collectorEnv,
        COLLECTOR_MODE: "real",
        COLLECTOR_CHALLENGE_WAIT_MS: "0",
        COLLECTOR_NOTIFY_COMMAND: "/usr/local/bin/aivc-notify",
      }),
    ).toMatchObject({
      COLLECTOR_CHALLENGE_WAIT_MS: 0,
      COLLECTOR_NOTIFY_COMMAND: "/usr/local/bin/aivc-notify",
    });
    expect(
      loadCollectorConfig({ ...collectorEnv, COLLECTOR_CHALLENGE_WAIT_MS: "1800000" })
        .COLLECTOR_CHALLENGE_WAIT_MS,
    ).toBe(1_800_000);
  });
  it("keeps mock and real mode working without a session profile", () => {
    const { COLLECTOR_SESSION_PROFILE_ID: _omitted, ...legacy } = collectorEnv;
    void _omitted;
    expect(loadCollectorConfig(legacy).COLLECTOR_SESSION_PROFILE_ID).toBeUndefined();
    // Control planes without an account pool (e.g. Jogeoai) keep the single-account flow.
    expect(
      loadCollectorConfig({ ...legacy, COLLECTOR_MODE: "real" }).COLLECTOR_SESSION_PROFILE_ID,
    ).toBeUndefined();
  });
  it("lets login run without API settings and requires them for a profile probe report", () => {
    const login = loadSessionConfig({ COLLECTOR_PROFILE_DIRECTORY: "/aivc/session" });
    expect(requireSessionReporting(login)).toBeUndefined();
    const profileOnly = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/session",
      COLLECTOR_SESSION_PROFILE_ID: collectorEnv.COLLECTOR_SESSION_PROFILE_ID,
    });
    expect(() => requireSessionReporting(profileOnly)).toThrow(/COLLECTOR_API_URL/);
    const full = loadSessionConfig({
      COLLECTOR_PROFILE_DIRECTORY: "/aivc/session",
      COLLECTOR_SESSION_PROFILE_ID: collectorEnv.COLLECTOR_SESSION_PROFILE_ID,
      COLLECTOR_API_URL: collectorEnv.COLLECTOR_API_URL,
      COLLECTOR_SERVICE_TOKEN: collectorEnv.COLLECTOR_SERVICE_TOKEN,
      COLLECTOR_TENANT_ID: collectorEnv.COLLECTOR_TENANT_ID,
      COLLECTOR_NODE_ID: collectorEnv.COLLECTOR_NODE_ID,
      COLLECTOR_REGION: "cn",
    });
    expect(requireSessionReporting(full)).toEqual({
      COLLECTOR_SESSION_PROFILE_ID: collectorEnv.COLLECTOR_SESSION_PROFILE_ID,
      COLLECTOR_API_URL: collectorEnv.COLLECTOR_API_URL,
      COLLECTOR_SERVICE_TOKEN: collectorEnv.COLLECTOR_SERVICE_TOKEN,
      COLLECTOR_TENANT_ID: collectorEnv.COLLECTOR_TENANT_ID,
      COLLECTOR_NODE_ID: collectorEnv.COLLECTOR_NODE_ID,
      COLLECTOR_REGION: "cn",
      COLLECTOR_HTTP_TIMEOUT_MS: 10_000,
    });
    expect(() =>
      loadSessionConfig({
        COLLECTOR_PROFILE_DIRECTORY: "/aivc/session",
        COLLECTOR_API_URL: "http://remote.example.com",
      }),
    ).toThrow();
    expect(() =>
      loadSessionConfig({
        COLLECTOR_PROFILE_DIRECTORY: "/aivc/session",
        COLLECTOR_SESSION_PROFILE_ID: "not-a-uuid",
      }),
    ).toThrow();
  });
});
