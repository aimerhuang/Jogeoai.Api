import type { Page } from "playwright";
import { z } from "zod";
import {
  ConsumerWebAdapter,
  pageContractSchema as sharedSchema,
  loadPageContract as loadContract,
} from "./consumer-web.ts";

export const KIMI_URL = "https://www.kimi.com/";

export const kimiPageContractSchema = sharedSchema.extend({
  origin: z.literal("https://www.kimi.com"),
  samplingMode: z.literal("default_conversation"),
});
export type KimiPageContract = z.infer<typeof kimiPageContractSchema>;

export async function loadKimiPageContract(path: string | undefined) {
  const contract = await loadContract(path, kimiPageContractSchema);
  return contract ? kimiPageContractSchema.parse(contract) : undefined;
}

export class KimiAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: KimiPageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    super(
      page,
      contract,
      "kimi",
      KIMI_URL,
      ["kimi.com", "www.kimi.com"],
      timing,
      undefined,
      true,
      15_000,
      undefined,
      undefined,
      true,
    );
  }
}
