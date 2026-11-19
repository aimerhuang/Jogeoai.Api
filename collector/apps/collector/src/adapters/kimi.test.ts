import { chromium } from "playwright";
import { describe, expect, it } from "vitest";
import { fixtureTask } from "../test-fixtures.ts";
import { buildWebQuestion } from "../web-question.ts";
import { KimiAdapter, kimiPageContractSchema, type KimiPageContract } from "./kimi.ts";

const contract: KimiPageContract = {
  version: 2,
  origin: "https://www.kimi.com",
  samplingMode: "default_conversation",
  reviewedAt: new Date().toISOString(),
  verifiedOnAuthorizedPage: true,
  selectors: {
    authenticated: ".user-info",
    newConversation: ".new-chat-btn",
    composer: ".chat-input-editor",
    conversationActive: ".chat-input-editor-container",
    submit: ".send-button-container button",
    assistantMessage: ".chat-content-item-assistant",
    answerBody:
      ".segment-assistant .toolcall-rollup__part:not(:has(.thinking-container)) .markdown",
    generating: ".segment-assistant.is-generating",
    completed: ".segment-assistant-actions",
    citations: ".segment-assistant a[href]",
    loginRequired: ".login-button",
    regionRestricted: "body:has-text('暂不支持')",
    challengeRequired: "#captcha_container iframe",
    rateLimited: "body:has-text('请求过于频繁')",
  },
};

const html = `<!doctype html><html><body>
<div class="user-info">登月者</div><button class="new-chat-btn" onclick="document.querySelector('.chat-content-list').replaceChildren()">新建会话</button>
<main class="chat-page chat"><div class="chat-content-list"></div>
<div class="chat-action"><div class="chat-input-editor-container"><div class="chat-input-editor" contenteditable="true"></div></div><div class="send-button-container"><button onclick="answer()">发送</button></div></div>
<script>function answer(){document.querySelector('.chat-content-list').innerHTML='<div class="chat-content-item-assistant"><div class="segment segment-assistant"><div class="segment-content"><div class="toolcall-rollup"><div class="toolcall-rollup__part"><div class="toolcall-container thinking-container"><div class="markdown">Thinking only.</div></div></div><div class="toolcall-rollup__part"><div class="markdown">Synthetic Kimi answer.</div></div></div></div><div class="segment-assistant-actions"><span>操作</span></div></div></div>'}</script>
</main></body></html>`;

describe("Kimi synthetic consumer-Web contract", () => {
  it.each(["normal", "pre-wrap"])(
    "preserves the question in a %s contenteditable and extracts the final answer",
    async (whiteSpace) => {
      expect(kimiPageContractSchema.parse(contract).origin).toBe("https://www.kimi.com");
      const browser = await chromium.launch({ headless: true });
      try {
        const page = await browser.newPage();
        const body = html.replace(
          'contenteditable="true"',
          `contenteditable="true" style="white-space: ${whiteSpace}"`,
        );
        await page.route("**/*", (route) => route.fulfill({ contentType: "text/html", body }));
        const adapter = new KimiAdapter(page, contract, {
          pollMs: 10,
          stableMs: 30,
          timeoutMs: 500,
        });
        expect(await adapter.probe(new AbortController().signal)).toEqual({ status: "ready" });
        const task = Object.freeze({
          ...fixtureTask(),
          platformCode: "kimi" as const,
          questionText: whiteSpace === "pre-wrap" ? "  中文  原文\nsecond line\n " : "中文原文",
        });
        const original = { ...task };
        const result = await adapter.collect(task, new AbortController().signal);
        const composer = page.locator(".chat-input-editor");
        // The readback must restore the original caret, not leave the prompt selected.
        expect(
          await composer.evaluate((element) => element.ownerDocument.getSelection()?.isCollapsed),
        ).toBe(true);
        await composer.press("ControlOrMeta+A");
        expect(
          await composer.evaluate((element) => element.ownerDocument.getSelection()?.toString()),
        ).toBe(buildWebQuestion(task.questionText));
        expect(task).toEqual(original);
        expect(result.answerText).toBe("Synthetic Kimi answer.");
        expect(result.citations).toEqual([]);
      } finally {
        await browser.close();
      }
    },
  );
  it.each(["content", "whitespace"])(
    "rejects %s changed by an input handler before submitting",
    async (mutation) => {
      const browser = await chromium.launch({ headless: true });
      try {
        const page = await browser.newPage();
        await page.route("**/*", (route) =>
          route.fulfill({ contentType: "text/html", body: html }),
        );
        const adapter = new KimiAdapter(page, contract, {
          pollMs: 10,
          stableMs: 30,
          timeoutMs: 500,
        });
        await adapter.probe(new AbortController().signal);
        await page.locator(".chat-input-editor").evaluate((element, mutation) => {
          element.addEventListener(
            "input",
            () => {
              if (mutation === "whitespace")
                element.innerHTML = element.innerHTML.replace("中文 原文", "中文\u00a0原文");
              else element.append(" altered");
            },
            { once: true },
          );
        }, mutation);
        await expect(
          adapter.collect(
            { ...fixtureTask(), platformCode: "kimi", questionText: "中文 原文" },
            new AbortController().signal,
          ),
        ).rejects.toMatchObject({ code: "page_contract_changed" });
        expect(await page.locator(".chat-content-item-assistant").count()).toBe(0);
      } finally {
        await browser.close();
      }
    },
  );
});
