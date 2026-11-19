import type { Page } from "playwright";
import { z } from "zod";
import {
  ConsumerWebAdapter,
  pageContractSchema as sharedSchema,
  loadPageContract as loadContract,
} from "./consumer-web.ts";

export const GEMINI_URL = "https://gemini.google.com/app";

// Selectors must be reviewed against an authorized persistent Profile before
// enabling a live overseas run. No default selector is guessed here.
export const geminiPageContractSchema = sharedSchema.extend({
  origin: z.literal("https://gemini.google.com"),
  samplingMode: z.literal("default_conversation"),
});
export type GeminiPageContract = z.infer<typeof geminiPageContractSchema>;

export async function loadGeminiPageContract(path: string | undefined) {
  const contract = await loadContract(path, geminiPageContractSchema);
  return contract ? geminiPageContractSchema.parse(contract) : undefined;
}

export class GeminiAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: GeminiPageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    super(
      page,
      contract,
      "gemini",
      GEMINI_URL,
      ["gemini.google.com", "google.com", "accounts.google.com"],
      timing,
      undefined,
      true,
      5_000,
      undefined,
      undefined,
      true,
      false,
    );
  }
}
