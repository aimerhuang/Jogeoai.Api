import { timingSafeEqual } from "node:crypto";

import { DomainError, type Region } from "@aivc/domain";

export interface CollectorServiceTokens {
  cn: string;
  overseas: string;
}

function tokenMatches(provided: string, expected: string): boolean {
  const providedBuffer = Buffer.from(provided);
  const expectedBuffer = Buffer.from(expected);
  return (
    providedBuffer.length === expectedBuffer.length &&
    timingSafeEqual(providedBuffer, expectedBuffer)
  );
}

export function authenticateCollector(
  authorization: string | undefined,
  tokens: CollectorServiceTokens,
): Region {
  const provided = authorization?.startsWith("Bearer ") ? authorization.slice(7) : "";
  const cnMatch = tokenMatches(provided, tokens.cn);
  const overseasMatch = tokenMatches(provided, tokens.overseas);

  if (cnMatch === overseasMatch) {
    throw new DomainError("UNAUTHORIZED", "Collector service authentication failed", 401);
  }
  return cnMatch ? "cn" : "overseas";
}

export function assertCollectorRegion(authorizedRegion: Region, claimedRegion: Region): void {
  if (authorizedRegion !== claimedRegion) {
    throw new DomainError(
      "COLLECTOR_REGION_FORBIDDEN",
      "Collector credentials are not authorized for the requested region",
      403,
    );
  }
}
