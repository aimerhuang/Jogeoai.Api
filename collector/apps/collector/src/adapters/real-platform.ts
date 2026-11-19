import type { loadSessionConfig } from "@aivc/config";
import type { Page } from "playwright";
import { DOUBAO_URL, DoubaoAdapter, loadPageContract } from "./doubao.ts";
import { ERNIE_URL, ErnieAdapter, loadErniePageContract } from "./ernie.ts";
import { QINGYAN_URL, QingyanAdapter, loadQingyanPageContract } from "./qingyan.ts";
import { QWEN_URL, QwenAdapter, loadQwenPageContract } from "./qwen.ts";
import { KIMI_URL, KimiAdapter, loadKimiPageContract } from "./kimi.ts";
import { DEEPSEEK_URL, DeepSeekAdapter, loadDeepSeekPageContract } from "./deepseek.ts";
import { YUANBAO_URL, YuanbaoAdapter, loadYuanbaoPageContract } from "./yuanbao.ts";
import { CHATGPT_URL, ChatGptAdapter, loadChatGptPageContract } from "./chatgpt.ts";
import { GEMINI_URL, GeminiAdapter, loadGeminiPageContract } from "./gemini.ts";

type SessionConfig = ReturnType<typeof loadSessionConfig>;
export function realPlatformUrl(config: SessionConfig): string {
  switch (config.COLLECTOR_SUPPORTED_PLATFORMS) {
    case "doubao":
      return DOUBAO_URL;
    case "ernie":
      return ERNIE_URL;
    case "zhipu_qingyan":
      return QINGYAN_URL;
    case "qwen":
      return QWEN_URL;
    case "kimi":
      return KIMI_URL;
    case "deepseek":
      return DEEPSEEK_URL;
    case "tencent_yuanbao":
      return YUANBAO_URL;
    case "chatgpt":
      return CHATGPT_URL;
    case "gemini":
      return GEMINI_URL;
    default:
      return unreachable(config.COLLECTOR_SUPPORTED_PLATFORMS);
  }
}
export async function createRealAdapter(page: Page, config: SessionConfig) {
  switch (config.COLLECTOR_SUPPORTED_PLATFORMS) {
    case "doubao":
      return new DoubaoAdapter(page, await loadPageContract(config.DOUBAO_PAGE_CONTRACT_PATH));
    case "ernie":
      return new ErnieAdapter(page, await loadErniePageContract(config.ERNIE_PAGE_CONTRACT_PATH));
    case "zhipu_qingyan":
      return new QingyanAdapter(
        page,
        await loadQingyanPageContract(config.QINGYAN_PAGE_CONTRACT_PATH),
      );
    case "qwen":
      return new QwenAdapter(page, await loadQwenPageContract(config.QWEN_PAGE_CONTRACT_PATH));
    case "kimi":
      return new KimiAdapter(page, await loadKimiPageContract(config.KIMI_PAGE_CONTRACT_PATH));
    case "deepseek":
      return new DeepSeekAdapter(
        page,
        await loadDeepSeekPageContract(config.DEEPSEEK_PAGE_CONTRACT_PATH),
      );
    case "tencent_yuanbao":
      return new YuanbaoAdapter(
        page,
        await loadYuanbaoPageContract(config.YUANBAO_PAGE_CONTRACT_PATH),
      );
    case "chatgpt":
      return new ChatGptAdapter(
        page,
        await loadChatGptPageContract(config.CHATGPT_PAGE_CONTRACT_PATH),
      );
    case "gemini":
      return new GeminiAdapter(
        page,
        await loadGeminiPageContract(config.GEMINI_PAGE_CONTRACT_PATH),
      );
    default:
      return unreachable(config.COLLECTOR_SUPPORTED_PLATFORMS);
  }
}

function unreachable(platform: never): never {
  void platform;
  throw new Error("unsupported_real_platform");
}
