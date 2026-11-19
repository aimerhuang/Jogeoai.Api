import { execFileSync } from "node:child_process";

import { describe, expect, it } from "vitest";

interface ComposeService {
  environment?: Record<string, string>;
  ports?: Array<{ published?: string | number; target?: number }>;
}

describe("local compose topology", () => {
  it("uses isolated ports and never gives collectors database credentials", () => {
    const rendered = execFileSync(
      "docker",
      ["compose", "--env-file", ".env.example", "config", "--format", "json"],
      { encoding: "utf8" },
    );
    const compose = JSON.parse(rendered) as { services: Record<string, ComposeService> };
    expect(Object.keys(compose.services).sort()).toEqual(
      ["api", "collector-cn", "collector-overseas", "postgres", "worker"].sort(),
    );
    expect(compose.services.postgres?.ports?.[0]?.published).toBe("55432");
    expect(compose.services.api?.ports?.[0]?.published).toBe("4311");
    expect(compose.services["collector-cn"]?.environment).not.toHaveProperty("DATABASE_URL");
    expect(compose.services["collector-overseas"]?.environment).not.toHaveProperty("DATABASE_URL");
    expect(compose.services["collector-cn"]?.environment?.COLLECTOR_REGION).toBe("cn");
    expect(compose.services["collector-overseas"]?.environment?.COLLECTOR_REGION).toBe("overseas");
    expect(compose.services["collector-cn"]?.environment?.COLLECTOR_SERVICE_TOKEN).toBe(
      compose.services.api?.environment?.COLLECTOR_CN_SERVICE_TOKEN,
    );
    expect(compose.services["collector-overseas"]?.environment?.COLLECTOR_SERVICE_TOKEN).toBe(
      compose.services.api?.environment?.COLLECTOR_OVERSEAS_SERVICE_TOKEN,
    );
    expect(compose.services["collector-cn"]?.environment?.COLLECTOR_SERVICE_TOKEN).not.toBe(
      compose.services["collector-overseas"]?.environment?.COLLECTOR_SERVICE_TOKEN,
    );
  });
});
