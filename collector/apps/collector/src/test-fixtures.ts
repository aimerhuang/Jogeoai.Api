import { loadCollectorConfig } from "@aivc/config";
import type { LeasedTask } from "@aivc/contracts";

export const collectorEnv = {
  COLLECTOR_API_URL: "http://127.0.0.1:4314",
  COLLECTOR_SERVICE_TOKEN: "test-collector-token-only-not-real",
  COLLECTOR_TENANT_ID: "11111111-1111-4111-8111-111111111111",
  COLLECTOR_EXPECTED_PROJECT_ID: "22222222-2222-4222-8222-222222222222",
  COLLECTOR_NODE_ID: "test-node",
  COLLECTOR_REGION: "cn",
  COLLECTOR_EGRESS_COUNTRY: "CN",
  COLLECTOR_SPOOL_KEY: Buffer.alloc(32, 3).toString("base64"),
  COLLECTOR_SUPPORTED_PLATFORMS: "doubao",
  COLLECTOR_CAPACITY: "1",
  COLLECTOR_RUN_ONCE: "true",
  COLLECTOR_SESSION_PROFILE_ID: "66666666-6666-4666-8666-666666666666",
};
export const fixtureConfig = () => loadCollectorConfig(collectorEnv);
export const fixtureTask = (): LeasedTask => ({
  id: "33333333-3333-4333-8333-333333333333",
  tenantId: collectorEnv.COLLECTOR_TENANT_ID,
  brandProjectId: collectorEnv.COLLECTOR_EXPECTED_PROJECT_ID,
  platformCode: "doubao",
  platformEntryCode: "doubao.consumer_web",
  platformEntryVersionId: "44444444-4444-4444-8444-444444444444",
  platformEntryVersion: 1,
  questionVersionId: "55555555-5555-4555-8555-555555555555",
  questionText: "管道内窥镜",
  region: "cn",
  language: "zh-CN",
  channel: "consumer_web",
  leaseVersion: 1,
  leaseExpiresAt: new Date(Date.now() + 60_000).toISOString(),
});
