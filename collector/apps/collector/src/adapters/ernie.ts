import type { Page } from "playwright";
import { z } from "zod";
import {
  ConsumerWebAdapter,
  pageContractSchema as sharedSchema,
  loadPageContract as loadContract,
} from "./consumer-web.ts";
import { readAnswerMarkdown } from "./answer-markdown.ts";

export const ERNIE_URL = "https://wenxin.baidu.com/";
// Selectors must be reviewed on the authorized page; fixtures are not live proof.
export const erniePageContractSchema = sharedSchema.extend({
  origin: z.literal("https://wenxin.baidu.com"),
  samplingMode: z.literal("quick_conversation"),
});
export type ErniePageContract = z.infer<typeof erniePageContractSchema>;
export async function loadErniePageContract(path: string | undefined) {
  const contract = await loadContract(path, erniePageContractSchema);
  return contract ? erniePageContractSchema.parse(contract) : undefined;
}
export class ErnieAdapter extends ConsumerWebAdapter {
  constructor(
    page: Page,
    contract?: ErniePageContract,
    timing?: { pollMs: number; stableMs: number; timeoutMs: number },
  ) {
    super(
      page,
      contract,
      "ernie",
      ERNIE_URL,
      ["wenxin.baidu.com", "yiyan.baidu.com", "passport.baidu.com"],
      timing,
      "data-long-press-ext-info",
      true,
      5_000,
      undefined,
      undefined,
      false,
      true,
      // 回答正文转为 Markdown（与豆包同一转换器）；为空或失败时沿用 innerText 纯文本。
      contract
        ? (message, timeoutMs) => readAnswerMarkdown(message, contract, timeoutMs)
        : undefined,
    );
  }
}
