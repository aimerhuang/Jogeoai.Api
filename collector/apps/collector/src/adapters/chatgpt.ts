import type { Page } from "playwright";
import { z } from "zod";
import {
  ConsumerWebAdapter,
  pageContractSchema as sharedSchema,
  loadPageContract as loadContract,
} from "./consumer-web.ts";

export const CHATGPT_URL = "https://chatgpt.com/";

// Selectors must be reviewed against an authorized persistent Profile before
// enabling a live overseas run. No default selector is guessed here.
export const chatgptPageContractSchema = sharedSchema.extend({
  origin: z.literal("https://chatgpt.com"),
  samplingMode: z.literal("default_conversation"),
});
export type ChatGptPageContract = z.infer<typeof chatgptPageContractSchema>;

export async function loadChatGptPageContract(path: string | undefined) {
  const contract = await loadContract(path, chatgptPageContractSchema);
  return contract ? chatgptPageContractSchema.parse(contract) : undefined;
}

export class ChatGptAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: ChatGptPageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    super(
      page,
      contract,
      "chatgpt",
      CHATGPT_URL,
      ["chatgpt.com", "www.chatgpt.com", "chat.openai.com", "openai.com"],
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
