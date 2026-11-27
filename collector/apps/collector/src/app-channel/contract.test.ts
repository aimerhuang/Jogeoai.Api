import { chmod, rm, symlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { parseBindings, resolveBindings } from "./bindings.ts";
import {
  ContractSource,
  appContractSchema,
  contractPath,
  contractRejection,
  isQuestionBubble,
  loadContractFile,
  sourcePaths,
  type AppContract,
} from "./contract.ts";
import {
  bindingsFixture,
  contractDirectory,
  iconAppContract,
  linksContract,
  referenceContract,
} from "./test-fixtures.ts";

const created: string[] = [];
afterEach(async () => {
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});
const binding = resolveBindings(parseBindings(bindingsFixture()))[0]!;
const NOW = new Date("2026-11-15T04:00:00.000Z");
const facts = { installedAppVersion: "15.7.0", deviceLocale: "zh-Hans-CN", now: NOW };
const parsed = (mutate: (contract: ReturnType<typeof referenceContract>) => void = () => {}) => {
  const contract = referenceContract();
  mutate(contract);
  return appContractSchema.parse(contract);
};

describe("App screen contract schema", () => {
  it("expresses the Doubao reference case", () => {
    const contract = parsed();
    expect(contract.newConversation.steps).toEqual([
      { tap: { id: "com.larus.nova:id/back_icon" } },
      { tap: { text: "新对话" } },
    ]);
    expect(contract.composer.prepare).toEqual([
      {
        tap: { id: "com.larus.nova:id/action_input" },
        unless: { id: "com.larus.nova:id/input_text" },
      },
    ]);
    expect(contract.completion).toEqual({
      reveal: [{ tap: { id: "com.larus.nova:id/fast_button_icon" }, optional: true }],
      marker: { id: "com.larus.nova:id/msg_action_regenerate" },
    });
    expect(contract.answer).toMatchObject({ method: "hierarchy", stitch: true });
    expect(contract.loggedIn.close).toEqual([{ back: true }]);
    expect(sourcePaths(contract)[0]?.searchQueries).toEqual({
      after: { id: "com.larus.nova:id/bottom_sheet_title" },
      exclude: [{ id: "com.larus.nova:id/tv_reference_index" }],
    });
  });
  it("expresses an App without resource IDs, with a copy control and a swipe to the end", () => {
    const contract = referenceContract();
    Object.assign(contract, {
      newConversation: {
        steps: [{ tap: { desc: "新建对话" } }, { waitFor: { textContains: "发消息" } }],
        emptyMarker: { className: "android.widget.EditText", textContains: "发消息" },
      },
      completion: {
        reveal: [{ swipe: "up", count: 2 }],
        marker: { desc: "重新生成回答" },
        generating: { desc: "停止生成" },
      },
      answer: { method: "copy", copy: { desc: "复制回答", pick: "last" } },
      citations: undefined,
      searchState: { deepThinkingOn: { text: "已深度思考" } },
    });
    expect(appContractSchema.safeParse(contract).success).toBe(true);
  });
  it("expresses an App whose controls carry no label and whose texts are descriptions", () => {
    const contract = appContractSchema.parse(iconAppContract());
    expect(contract.composer.send).toEqual({
      className: "android.widget.ImageView",
      unlabeled: true,
      clickable: false,
    });
    expect(contract.completion.marker).toMatchObject({ unlabeled: true, row: { size: 3, at: 0 } });
    expect(sourcePaths(contract)[0]!.cards).toMatchObject({ from: "desc" });
    expect(sourcePaths(contract)[0]!.entry).toEqual({ descMatches: "[0-9]+个来源" });
  });
  it("takes no row condition where one view at a time is asked whether it matches", () => {
    const row = { className: "android.widget.ImageView", row: { size: 3, at: 0 } };
    const refused = (mutate: (contract: ReturnType<typeof referenceContract>) => void) => {
      const contract = referenceContract();
      mutate(contract);
      return !appContractSchema.safeParse(contract).success;
    };
    expect(refused((contract) => void (contract.answer.end = row))).toBe(true);
    expect(refused((contract) => void (contract.citations.itemTitle = row))).toBe(true);
    expect(
      refused(
        (contract) =>
          void (contract.citations.searchQueries = {
            after: row,
          }),
      ),
    ).toBe(true);
    expect(
      refused(
        (contract) =>
          void (contract.citations.searchQueries = {
            after: { id: "com.larus.nova:id/bottom_sheet_title" },
            exclude: [row],
          }),
      ),
    ).toBe(true);
    // Where a screen is searched, a row is a condition like any other.
    expect(refused((contract) => void (contract.answer.exclude = [row]))).toBe(false);
    expect(refused((contract) => void (contract.completion.marker = row))).toBe(false);
    expect(refused((contract) => void (contract.citations.entry = row))).toBe(false);
  });
  it("requires a count pattern to capture exactly one number", () => {
    const withCount = (pattern: string) => {
      const contract = referenceContract();
      Object.assign(contract.citations, { count: { from: "entry", pattern } });
      return appContractSchema.safeParse(contract).success;
    };
    expect(withCount("(\\d+)\\s*个网页")).toBe(true);
    expect(withCount("\\d+\\s*个网页")).toBe(false);
    expect(withCount("(\\d+)(\\s*)个网页")).toBe(false);
    expect(withCount("(\\d+")).toBe(false);
  });

  it("takes exactly one way to tell sources apart, and one way to see an empty conversation", () => {
    const accepts = (mutate: (contract: ReturnType<typeof referenceContract>) => void) => {
      const contract = referenceContract();
      mutate(contract);
      return appContractSchema.safeParse(contract).success;
    };
    const cards = { layout: [{ name: "site" }, { name: "title" }] };
    // Cards instead of a title selector; a panel without a close control; a stated number.
    expect(
      accepts((contract) => {
        delete contract.citations.itemTitle;
        Object.assign(contract.citations, {
          cards,
          close: { back: true },
          count: {
            from: "panel",
            selector: { textContains: "引用来源" },
            pattern: "引用来源\\s*(\\d+)",
          },
        });
      }),
    ).toBe(true);
    // Both, neither, or card slots mixed with title-relative selectors.
    expect(accepts((contract) => void Object.assign(contract.citations, { cards }))).toBe(false);
    expect(accepts((contract) => void delete contract.citations.itemTitle)).toBe(false);
    expect(
      accepts((contract) => {
        delete contract.citations.itemTitle;
        Object.assign(contract.citations, { cards, itemSite: { text: "合成站点" } });
      }),
    ).toBe(false);
    // A number stated by the panel needs the view that states it; back is the only key press.
    expect(
      accepts(
        (contract) =>
          void Object.assign(contract.citations, { count: { from: "panel", pattern: "(\\d+)" } }),
      ),
    ).toBe(false);
    expect(
      accepts((contract) => void Object.assign(contract.citations, { close: { back: false } })),
    ).toBe(false);

    const conversation = (value: Record<string, unknown>) =>
      accepts((contract) => void Object.assign(contract, { newConversation: value }));
    const steps = [{ tap: { id: "chat_new" } }];
    expect(conversation({ steps, emptyWhenAbsent: [{ desc: "重新生成" }] })).toBe(true);
    expect(conversation({ steps })).toBe(false);
    expect(conversation({ steps, emptyWhenAbsent: [] })).toBe(false);

    // The end of the answer text and the question bubble are selectors of the hierarchy method
    // only.
    expect(
      accepts((contract) => void Object.assign(contract.answer, { end: { desc: "复制" } })),
    ).toBe(true);
    expect(
      accepts(
        (contract) =>
          void Object.assign(contract.answer, { question: { id: "com.larus.nova:id/send_text" } }),
      ),
    ).toBe(true);
    expect(accepts((contract) => void Object.assign(contract.answer, { question: {} }))).toBe(
      false,
    );
    // An App without resource IDs on its message views: the bubble is the text view that has no
    // content description.
    const bubble = { className: "android.widget.TextView", withoutDesc: true } as const;
    expect(accepts((contract) => void Object.assign(contract.answer, { question: bubble }))).toBe(
      true,
    );
    for (const question of [{ withoutDesc: false }, { text: "合成问题" }, { desc: "" }])
      expect(accepts((contract) => void Object.assign(contract.answer, { question }))).toBe(false);
    const view = (desc: string, className = "android.widget.TextView") => ({
      resourceId: "",
      className,
      desc,
    });
    expect(isQuestionBubble(view(""), bubble)).toBe(true);
    // The answer's text view repeats its text as its description.
    expect(isQuestionBubble(view("合成回答"), bubble)).toBe(false);
    expect(isQuestionBubble(view("", "android.widget.Button"), bubble)).toBe(false);
    expect(
      isQuestionBubble(
        { ...view("合成描述"), resourceId: "com.larus.nova:id/send_text" },
        { id: "com.larus.nova:id/send_text" },
      ),
    ).toBe(true);
    // Required with the hierarchy method: without it an answer line equal to the question
    // could pass for the bubble.
    expect(
      accepts((contract) => void delete (contract.answer as { question?: unknown }).question),
    ).toBe(false);
    expect(
      accepts(
        (contract) =>
          void (contract.answer = {
            method: "copy",
            copy: { desc: "复制" },
            question: { id: "com.larus.nova:id/send_text" },
          }),
      ),
    ).toBe(false);
    expect(
      accepts(
        (contract) =>
          void (contract.answer = {
            method: "copy",
            copy: { desc: "复制" },
            end: { desc: "复制" },
          }),
      ),
    ).toBe(false);
  });
  it("expresses a source entry that opens a screen between itself and the source lists", () => {
    const accepts = (mutate: (citations: Record<string, unknown>) => void = () => {}) => {
      const contract = referenceContract();
      contract.citations = {
        entry: { textMatches: "使用 \\d+ 个工具.*", pick: "first" },
        entrySwipe: { swipe: "down", max: 40, stopWhenUnmoved: 2 },
        via: {
          panel: { text: "工具调用" },
          item: { textMatches: "搜索网页.*" },
          queryPrefix: "搜索网页",
          close: { back: true },
        },
        panel: { text: "网页搜索" },
        cards: { layout: [{ name: "title" }, { name: "site", optional: true }], sameHeight: true },
        close: { back: true },
      };
      mutate(contract.citations);
      return appContractSchema.safeParse(contract).success;
    };
    const via = (value: Record<string, unknown>) =>
      accepts((citations) => void Object.assign(citations.via as object, value));
    const entrySwipe = (value: Record<string, unknown>) =>
      accepts((citations) => void (citations.entrySwipe = { swipe: "down", ...value }));
    expect(accepts()).toBe(true);
    const parsedVia = sourcePaths(
      parsed((contract) => {
        Object.assign(contract.citations, {
          via: {
            panel: { text: "工具调用" },
            item: { textContains: "搜索网页" },
            close: { back: true },
          },
        });
      }),
    )[0]!.via!;
    // What is left out stays out: the flow supplies the defaults.
    expect(parsedVia).toEqual({
      panel: { text: "工具调用" },
      item: { textContains: "搜索网页" },
      close: { back: true },
    });

    // The screen in between: its marker, its rows and a way out are required.
    expect(via({ close: { desc: "关闭" }, maxItems: 10 })).toBe(true);
    expect(via({ maxItems: 1 })).toBe(true);
    expect(via({ maxItems: 0 })).toBe(false);
    expect(via({ maxItems: 11 })).toBe(false);
    expect(via({ queryPrefix: "" })).toBe(false);
    expect(via({ item: { textMatches: "(搜索网页" } })).toBe(false);
    expect(via({ close: { back: false } })).toBe(false);
    expect(via({ scroll: true })).toBe(false);
    for (const key of ["panel", "item", "close"])
      expect(
        accepts((citations) => void delete (citations.via as Record<string, unknown>)[key]),
      ).toBe(false);
    // Several lists are joined, so no stated number can be checked against them.
    expect(
      accepts(
        (citations) => void (citations.count = { from: "entry", pattern: "使用 (\\d+) 个工具" }),
      ),
    ).toBe(false);
    expect(
      accepts(
        (citations) =>
          void (citations.count = {
            from: "panel",
            selector: { text: "网页搜索" },
            pattern: "(\\d+)",
          }),
      ),
    ).toBe(false);

    // Swipes to the entry: up to 40, and from 2 to 10 unchanged ones to stop early.
    expect(entrySwipe({ max: 40 })).toBe(true);
    expect(entrySwipe({ max: 41 })).toBe(false);
    expect(entrySwipe({ max: 0 })).toBe(false);
    expect(entrySwipe({ max: 40, stopWhenUnmoved: 10 })).toBe(true);
    expect(entrySwipe({ max: 40, stopWhenUnmoved: 1 })).toBe(false);
    expect(entrySwipe({ max: 40, stopWhenUnmoved: 11 })).toBe(false);
    expect(entrySwipe({ max: 40, stopWhenUnmoved: 2.5 })).toBe(false);
    expect(entrySwipe({ max: 40, stopAtEnd: true })).toBe(false);
    expect(entrySwipe({ stopWhenUnmoved: 2 })).toBe(false);
    // Without a screen in between, a stated number is still allowed.
    expect(
      accepts((citations) => {
        delete citations.via;
        citations.count = { from: "entry", pattern: "使用 (\\d+) 个工具" };
      }),
    ).toBe(true);
  });
  // Task 11-24: every source opened to read its link.
  it("expresses reading every source's link through its page, with title selectors only", () => {
    const accepts = (mutate: (contract: ReturnType<typeof linksContract>) => void = () => {}) => {
      const contract = linksContract();
      mutate(contract);
      return appContractSchema.safeParse(contract).success;
    };
    const links = (value: Record<string, unknown>) =>
      accepts((contract) => void Object.assign(contract.citations.links as object, value));
    expect(accepts()).toBe(true);
    // What is left out stays out: the flow supplies the default budget.
    expect(sourcePaths(appContractSchema.parse(linksContract()))[0]!.links).toEqual({
      page: { id: "com.larus.nova:id/bullet_container" },
      share: { id: "com.larus.nova:id/btn_share" },
      copy: { text: "复制链接" },
      close: { back: true },
      unwrap: [{ host: "link.wtturl.cn", param: "target" }],
      dropParams: ["use_xbridge3", "loader_name", "need_sec_link", "sec_link_scene", "theme"],
      systemShare: {
        package: "com.android.intentresolver",
        copy: { id: "com.android.intentresolver:id/copy" },
      },
    });
    // Without links a path is what it was.
    expect(sourcePaths(parsed())[0]).not.toHaveProperty("links");

    // Page, share, copy and a way out are required; restoration rules are not.
    for (const key of ["page", "share", "copy", "close"])
      expect(
        accepts(
          (contract) => void delete (contract.citations.links as Record<string, unknown>)[key],
        ),
      ).toBe(false);
    expect(
      accepts((contract) => {
        const value = contract.citations.links as Record<string, unknown>;
        delete value.unwrap;
        delete value.dropParams;
      }),
    ).toBe(true);
    expect(links({ close: { desc: "关闭" } })).toBe(true);
    expect(links({ close: { back: false } })).toBe(false);
    expect(links({ page: {} })).toBe(false);
    expect(links({ scroll: true })).toBe(false);

    // Budget: 30 to 600 whole seconds.
    expect(links({ budgetSeconds: 30 })).toBe(true);
    expect(links({ budgetSeconds: 600 })).toBe(true);
    expect(links({ budgetSeconds: 29 })).toBe(false);
    expect(links({ budgetSeconds: 601 })).toBe(false);
    expect(links({ budgetSeconds: 90.5 })).toBe(false);

    // Host names in lower case, parameter names of letters, digits and `_.-`.
    const unwrap = (host: string, param = "target") => links({ unwrap: [{ host, param }] });
    expect(unwrap("link.example.cn")).toBe(true);
    expect(unwrap("a-b.example.cn", "t_1.x-y")).toBe(true);
    for (const host of [
      "Link.Example.cn",
      "example",
      "https://link.example.cn",
      "link.example.cn/",
      "-a.example.cn",
      "a..example.cn",
      "例子.cn",
      "",
    ])
      expect(unwrap(host)).toBe(false);
    for (const param of ["", "a b", "a=b", "a&b", "目标", "x".repeat(65)])
      expect(unwrap("link.example.cn", param)).toBe(false);
    expect(links({ unwrap: [{ host: "link.example.cn" }] })).toBe(false);
    expect(links({ unwrap: [{ host: "link.example.cn", param: "target", extra: 1 }] })).toBe(false);
    expect(
      links({
        unwrap: Array.from({ length: 6 }, (_, index) => ({
          host: `l${index}.example.cn`,
          param: "t",
        })),
      }),
    ).toBe(false);
    expect(links({ dropParams: ["theme", "x".repeat(64)] })).toBe(true);
    expect(links({ dropParams: ["the me"] })).toBe(false);
    expect(links({ dropParams: Array.from({ length: 21 }, (_, index) => `p${index}`) })).toBe(
      false,
    );

    // Android's own share sheet (task 11-24 §11.1): a package name and its copy button, both
    // required; optional as a whole.
    const systemShare = (value: unknown) => links({ systemShare: value });
    expect(
      accepts(
        (contract) => void delete (contract.citations.links as Record<string, unknown>).systemShare,
      ),
    ).toBe(true);
    expect(systemShare({ package: "com.android.intentresolver", copy: { text: "复制" } })).toBe(
      true,
    );
    for (const name of ["", "intentresolver", "com.android.", "1com.android.x", "com android.x"])
      expect(systemShare({ package: name, copy: { id: "a:id/copy" } }), name).toBe(false);
    expect(systemShare({ package: "com.android.intentresolver" })).toBe(false);
    expect(systemShare({ copy: { id: "a:id/copy" } })).toBe(false);
    expect(systemShare({ package: "com.android.intentresolver", copy: {} })).toBe(false);
    expect(
      systemShare({
        package: "com.android.intentresolver",
        copy: { id: "a" },
        preview: { id: "b" },
      }),
    ).toBe(false);

    // Not with source cards, and not behind a screen in between.
    const withCards = appContractSchema.safeParse(
      (() => {
        const contract = linksContract();
        delete contract.citations.itemTitle;
        delete contract.citations.searchQueries;
        contract.citations.cards = { layout: [{ name: "title" }] };
        return contract;
      })(),
    );
    expect(withCards.success).toBe(false);
    expect(withCards.error?.issues).toEqual([
      expect.objectContaining({
        path: ["citations", "links"],
        message: "cannot be combined with cards",
      }),
    ]);
    const withVia = appContractSchema.safeParse(
      (() => {
        const contract = linksContract();
        contract.citations.via = {
          panel: { text: "工具调用" },
          item: { textContains: "搜索网页" },
          close: { back: true },
        };
        return contract;
      })(),
    );
    expect(withVia.success).toBe(false);
    expect(withVia.error?.issues).toEqual([
      expect.objectContaining({
        path: ["citations", "links"],
        message: "cannot be combined with via",
      }),
    ]);
    // A path of several may read links as well.
    expect(
      accepts((contract) => {
        const path = { ...contract.citations, label: "cited" };
        contract.citations = [path, { ...referenceContract().citations, label: "other" }] as never;
      }),
    ).toBe(true);
  });
  // Design §16.7: an answer that shows several source lists behind different entries.
  it("expresses an answer with several source lists, each path with a label of its own", () => {
    const search = {
      entry: { textMatches: "使用 [0-9]+ 个工具.*", pick: "first" },
      entrySwipe: { swipe: "down", max: 40, stopWhenUnmoved: 2 },
      via: {
        panel: { text: "工具调用" },
        item: { textMatches: "搜索网页.*" },
        queryPrefix: "搜索网页",
        close: { back: true },
      },
      panel: { text: "网页搜索" },
      cards: { layout: [{ name: "title" }, { name: "site", optional: true }], sameHeight: true },
      close: { back: true },
    };
    // The reference path under the given label, and the search path under another.
    const check = (
      labels: (string | undefined)[],
      mutate: (paths: Record<string, unknown>[]) => void = () => {},
    ) => {
      const contract = referenceContract();
      const paths = labels.map((label, index) => ({
        ...(index === 0 ? contract.citations : search),
        ...(label === undefined ? {} : { label }),
      }));
      mutate(paths);
      (contract as { citations: unknown }).citations = paths;
      return appContractSchema.safeParse(contract);
    };
    const accepts = (...labels: (string | undefined)[]) => check(labels).success;
    const two = check(["cited", "search"]);
    expect(two.success).toBe(true);
    // The order written is the order read.
    expect(sourcePaths(two.data!).map((path) => path.label)).toEqual(["cited", "search"]);
    expect(sourcePaths(two.data!)[1]!.via).toEqual(search.via);
    expect(accepts("cited", "search", "more")).toBe(true);
    // One path is written as an object; more than three are not read.
    expect(accepts("cited")).toBe(false);
    expect(accepts()).toBe(false);
    expect(accepts("cited", "search", "more", "other")).toBe(false);
    // Every path of several needs a label, and no two the same.
    expect(accepts("cited", undefined)).toBe(false);
    expect(accepts("cited", "cited")).toBe(false);
    for (const label of ["Search", "1st", "web-search", "a".repeat(21), "合成", ""])
      expect(accepts("cited", label), label).toBe(false);
    expect(accepts("a", "a".repeat(20))).toBe(true);
    expect(accepts("list_1", "list_2")).toBe(true);
    // A path of several is a whole path: the same fields and the same rules as a single one.
    expect(check(["cited", "search"], (paths) => void delete paths[1]!.panel).success).toBe(false);
    expect(check(["cited", "search"], (paths) => void (paths[0]!.order = 1)).success).toBe(false);
    const counted = check(
      ["cited", "search"],
      (paths) => void (paths[1]!.count = { from: "entry", pattern: "使用 ([0-9]+) 个工具" }),
    );
    expect(counted.success).toBe(false);
    // The mistake is reported at its place in the path, not as "neither form fits".
    expect(counted.error!.issues).toEqual([
      expect.objectContaining({
        path: ["citations", 1],
        message: "via and count cannot be combined",
      }),
    ]);
    expect(check(["cited", undefined]).error!.issues).toEqual([
      expect.objectContaining({ path: ["citations", 1, "label"] }),
    ]);
    // Two labels left out are two labels left out, not also one label given twice.
    expect(check([undefined, undefined]).error!.issues).toEqual([
      expect.objectContaining({ path: ["citations", 0, "label"] }),
      expect.objectContaining({ path: ["citations", 1, "label"] }),
    ]);
    // So is an unknown key, in a path and in a part of it, and a part that is wrong.
    const issuesOf = (mutate: (paths: Record<string, unknown>[]) => void) =>
      check(["cited", "search"], mutate).error!.issues.map((issue) => ({
        path: issue.path,
        message: issue.message,
      }));
    expect(issuesOf((paths) => void (paths[0]!.order = 1))).toEqual([
      { path: ["citations", 0], message: expect.stringContaining('"order"') },
    ]);
    expect(
      issuesOf((paths) => void (paths[1]!.via = { ...(paths[1]!.via as object), scroll: true })),
    ).toEqual([{ path: ["citations", 1, "via"], message: expect.stringContaining('"scroll"') }]);
    expect(issuesOf((paths) => void (paths[1]!.close = { back: false }))).toEqual([
      { path: ["citations", 1, "close"], message: expect.any(String) },
    ]);
    expect(issuesOf((paths) => void (paths[0]!.entry = {}))).toEqual([
      { path: ["citations", 0, "entry"], message: "needs at least one condition" },
    ]);
    // What the contract holds is the checked value, not the value it was given.
    const given = [
      { ...referenceContract().citations, label: "cited" },
      { ...search, label: "search" },
    ];
    const held = appContractSchema.parse({ ...referenceContract(), citations: given }).citations;
    expect(held).toEqual(given);
    expect(held).not.toBe(given);
    expect((held as unknown[])[1]).not.toBe(given[1]);

    // A single path takes no label: its record names no list.
    const single = referenceContract();
    expect(appContractSchema.safeParse(single).success).toBe(true);
    expect(sourcePaths(appContractSchema.parse(single))).toHaveLength(1);
    expect(appContractSchema.parse(single).citations).toEqual(single.citations);
    expect(appContractSchema.parse(single).citations).not.toBe(single.citations);
    // An unknown key is refused in this form as well, at its place.
    const unknownKey = appContractSchema.safeParse({
      ...referenceContract(),
      citations: { ...referenceContract().citations, order: 1 },
    });
    expect(unknownKey.error!.issues).toEqual([
      expect.objectContaining({ path: ["citations"], message: expect.stringContaining('"order"') }),
    ]);
    Object.assign(single.citations, { label: "cited" });
    const labelled = appContractSchema.safeParse(single);
    expect(labelled.success).toBe(false);
    expect(labelled.error!.issues).toEqual([
      expect.objectContaining({ path: ["citations", "label"] }),
    ]);
    // No source section at all: no path.
    const none = referenceContract() as Record<string, unknown>;
    delete none.citations;
    expect(sourcePaths(appContractSchema.parse(none))).toEqual([]);
    for (const value of [null, "cited", 2, []])
      expect(
        appContractSchema.safeParse({ ...referenceContract(), citations: value }).success,
      ).toBe(false);
  });
  it("is strict: unknown keys, missing parts and selectors without a condition are refused", () => {
    const broken: ((contract: Record<string, unknown>) => void)[] = [
      (contract) => void (contract.activity = ".MainActivity"),
      (contract) => void delete contract.blockers,
      (contract) => void delete contract.popups,
      (contract) => void (contract.version = 2),
      (contract) => void (contract.platformCode = "not_a_platform"),
      (contract) => void (contract.appVersions = []),
      (contract) => void (contract.appVersions = ["15.7.0", "15.7.0"]),
      (contract) => void (contract.reviewedAt = "2026-11-14"),
      (contract) => void ((contract.composer as Record<string, unknown>).input = {}),
      (contract) => void ((contract.composer as Record<string, unknown>).input = { xpath: "//*" }),
      (contract) => void ((contract.blockers as Record<string, unknown>).captcha_solver = []),
      (contract) => void ((contract.newConversation as Record<string, unknown>).steps = []),
      (contract) =>
        void ((contract.newConversation as Record<string, unknown>).steps = [{ scrollTo: {} }]),
      // More swipes in one step than a gesture call allows.
      (contract) =>
        void ((contract.completion as Record<string, unknown>).reveal = [
          { swipe: "up", count: 11 },
        ]),
      // A poll must not depend on a control that may be absent.
      (contract) =>
        void ((contract.completion as Record<string, unknown>).reveal = [
          { tap: { id: "com.larus.nova:id/fast_button_icon" } },
        ]),
      (contract) => void ((contract.answer as Record<string, unknown>).method = "ocr"),
      (contract) =>
        void ((contract.citations as Record<string, unknown>).searchQueries = {
          selector: { text: "a" },
          after: { text: "b" },
        }),
    ];
    for (const mutate of broken) {
      const contract = referenceContract();
      mutate(contract);
      expect(appContractSchema.safeParse(contract).success).toBe(false);
    }
  });
  it("accepts empty blocker lists as 'none observed' but requires all four classes", () => {
    const contract = referenceContract() as Record<string, unknown>;
    contract.blockers = {
      login_required: [],
      challenge_required: [],
      rate_limited: [],
      emulator_rejected: [],
    };
    expect(appContractSchema.safeParse(contract).success).toBe(true);
    contract.blockers = { login_required: [], challenge_required: [], rate_limited: [] };
    expect(appContractSchema.safeParse(contract).success).toBe(false);
  });
  it("takes optional notices after the send, each a marker that is a rate limit or a declined question", () => {
    const notice = (as: string) => ({
      marker: { className: "android.widget.TextView", text: "合成提示", clickable: false },
      as,
    });
    const withNotices = (notices: unknown) => {
      const contract = referenceContract();
      contract.completion.notices = notices;
      return appContractSchema.safeParse(contract);
    };
    // Absent: none observed, as in every contract written before notices existed.
    expect(appContractSchema.parse(referenceContract()).completion.notices).toBeUndefined();
    const parsed = withNotices([notice("rate_limited"), notice("declined")]);
    expect(parsed.success && parsed.data.completion.notices?.map((n) => n.as)).toEqual([
      "rate_limited",
      "declined",
    ]);
    expect(withNotices([]).success).toBe(true);
    // Only the two classes; a login or verification screen stays a blocker.
    expect(withNotices([notice("login_required")]).success).toBe(false);
    expect(withNotices([{ ...notice("declined"), tap: true }]).success).toBe(false);
    expect(withNotices([{ as: "declined" }]).success).toBe(false);
    expect(withNotices(Array.from({ length: 6 }, () => notice("declined"))).success).toBe(false);
  });
});

// AC-A8: the installed version, the binding's reviewed version and the contract must agree.
describe("contract verification", () => {
  it("accepts a fresh contract for the installed version and the device language", () => {
    expect(contractRejection(parsed(), binding, facts)).toBeNull();
    // Seven days old is still fresh; one minute of clock skew into the future is tolerated.
    for (const reviewedAt of ["2026-11-08T04:00:00.000Z", "2026-11-15T04:00:59.000Z"])
      expect(
        contractRejection(
          parsed((contract) => void (contract.reviewedAt = reviewedAt)),
          binding,
          facts,
        ),
      ).toBeNull();
  });
  type Mutate = (contract: ReturnType<typeof referenceContract>) => void;
  const rejections: [string, Partial<typeof facts>, Mutate | undefined][] = [
    ["version_mismatch", { installedAppVersion: "15.8.0" }, undefined],
    ["version_mismatch", {}, (contract) => void (contract.appVersions = ["15.6.0"])],
    ["locale_mismatch", { deviceLocale: "en-US" }, undefined],
    ["review_expired", {}, (contract) => void (contract.reviewedAt = "2026-11-08T03:59:59.000Z")],
    ["review_in_future", {}, (contract) => void (contract.reviewedAt = "2026-11-15T04:01:01.000Z")],
    ["package_mismatch", {}, (contract) => void (contract.package = "com.example.other")],
    ["platform_mismatch", {}, (contract) => void (contract.platformCode = "kimi")],
  ];
  it.each(rejections)("rejects with %s", (reason, overrides, mutate) => {
    expect(contractRejection(parsed(mutate), binding, { ...facts, ...overrides })).toBe(reason);
  });
  it("accepts a contract reviewed for several versions only for the installed one", () => {
    const contract = parsed((value) => void (value.appVersions = ["15.6.0", "15.7.0"]));
    expect(contractRejection(contract, binding, facts)).toBeNull();
    // The binding's reviewed version is part of the agreement.
    expect(contractRejection(contract, { ...binding, reviewedAppVersion: "15.6.0" }, facts)).toBe(
      "version_mismatch",
    );
  });
});

describe("contract files", () => {
  it("loads <platformCode>.json from the private directory", async () => {
    const directory = await contractDirectory(created, { doubao: referenceContract() });
    expect(contractPath(directory, "doubao")).toBe(join(directory, "doubao.json"));
    const contract: AppContract = await loadContractFile(directory, "doubao");
    expect(contract.package).toBe("com.larus.nova");
  });
  it("rejects a missing, non-private, symlinked, oversized, malformed or invalid file", async () => {
    const directory = await contractDirectory(created, { doubao: referenceContract() });
    const path = join(directory, "doubao.json");
    await expect(loadContractFile(directory, "kimi")).rejects.toMatchObject({ reason: "missing" });

    await chmod(path, 0o644);
    await expect(loadContractFile(directory, "doubao")).rejects.toMatchObject({
      reason: "unreadable",
    });
    await chmod(path, 0o600);

    await symlink(path, join(directory, "kimi.json"));
    await expect(loadContractFile(directory, "kimi")).rejects.toMatchObject({
      reason: "unreadable",
    });

    await writeFile(path, " ".repeat(300 * 1024));
    await expect(loadContractFile(directory, "doubao")).rejects.toMatchObject({
      reason: "unreadable",
    });
    await writeFile(path, "{not json");
    await expect(loadContractFile(directory, "doubao")).rejects.toMatchObject({
      reason: "invalid",
    });
    await writeFile(path, JSON.stringify({ ...referenceContract(), secretNote: "x" }));
    const rejection = await loadContractFile(directory, "doubao").catch((error) => error);
    expect(rejection).toMatchObject({ reason: "invalid" });
    // Validator paths and messages only: no value of the file is repeated.
    expect(JSON.stringify(rejection.issues)).not.toContain('x"');
    expect(rejection.issues.length).toBeGreaterThan(0);
  });
  it("turns every rejection into app_contract_unverified and logs the reason", async () => {
    const directory = await contractDirectory(created, {
      doubao: referenceContract("2026-10-01T00:00:00.000Z"),
    });
    const logs: Record<string, unknown>[] = [];
    const source = ContractSource.fromDirectory(directory, (line) => logs.push(line));
    await expect(source.verified(binding, facts)).rejects.toMatchObject({
      code: "app_contract_unverified",
      phase: "pre_send",
    });
    const kimi = resolveBindings(parseBindings(bindingsFixture()))[1]!;
    await expect(
      source.verified(kimi, { ...facts, installedAppVersion: "3.1.8" }),
    ).rejects.toMatchObject({ code: "app_contract_unverified" });
    expect(logs).toEqual([
      {
        event: "app_contract_rejected",
        device: "AI_Apps_4KB",
        platform: "doubao",
        reason: "review_expired",
      },
      {
        event: "app_contract_rejected",
        device: "AI_Apps_4KB",
        platform: "kimi",
        reason: "missing",
      },
    ]);
  });
  it("reads the file again on every use, so a new review is picked up without a restart", async () => {
    const directory = await contractDirectory(created, {
      doubao: referenceContract("2026-10-01T00:00:00.000Z"),
    });
    const source = ContractSource.fromDirectory(directory);
    await expect(source.verified(binding, facts)).rejects.toMatchObject({
      code: "app_contract_unverified",
    });
    await writeFile(join(directory, "doubao.json"), JSON.stringify(referenceContract()));
    expect((await source.verified(binding, facts)).platformCode).toBe("doubao");
  });
});
