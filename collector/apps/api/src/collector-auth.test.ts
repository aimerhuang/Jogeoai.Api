import { describe, expect, it } from "vitest";

import { assertCollectorRegion, authenticateCollector } from "./collector-auth.ts";

const tokens = {
  cn: "cn-test-token-with-at-least-24-characters",
  overseas: "overseas-test-token-with-at-least-24-characters",
};

describe("collector service authentication", () => {
  it("binds each service token to exactly one region", () => {
    expect(authenticateCollector(`Bearer ${tokens.cn}`, tokens)).toBe("cn");
    expect(authenticateCollector(`Bearer ${tokens.overseas}`, tokens)).toBe("overseas");
  });

  it("rejects missing, invalid, and ambiguous credentials", () => {
    expect(() => authenticateCollector(undefined, tokens)).toThrowError(
      expect.objectContaining({ code: "UNAUTHORIZED", statusCode: 401 }),
    );
    expect(() => authenticateCollector("Bearer invalid", tokens)).toThrowError(
      expect.objectContaining({ code: "UNAUTHORIZED", statusCode: 401 }),
    );
    expect(() =>
      authenticateCollector(`Bearer ${tokens.cn}`, { cn: tokens.cn, overseas: tokens.cn }),
    ).toThrowError(expect.objectContaining({ code: "UNAUTHORIZED", statusCode: 401 }));
  });

  it("rejects a token identity claiming the other region", () => {
    expect(() => assertCollectorRegion("cn", "overseas")).toThrowError(
      expect.objectContaining({ code: "COLLECTOR_REGION_FORBIDDEN", statusCode: 403 }),
    );
    expect(() => assertCollectorRegion("overseas", "cn")).toThrowError(
      expect.objectContaining({ code: "COLLECTOR_REGION_FORBIDDEN", statusCode: 403 }),
    );
    expect(() => assertCollectorRegion("cn", "cn")).not.toThrow();
  });
});
