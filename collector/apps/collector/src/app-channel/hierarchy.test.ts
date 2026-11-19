import { describe, expect, it } from "vitest";
import { inRow, selectorSchema, viewSelectorSchema } from "./driver.ts";
import {
  descendants,
  findNodes,
  isInside,
  isReadable,
  layoutSignature,
  matchesSelector,
  mergeOverlap,
  mergeWindows,
  parseBounds,
  parseHierarchy,
} from "./hierarchy.ts";

// Shaped like a UiAutomator2 export; every value is synthetic.
const XML = `<?xml version='1.0' encoding='UTF-8' standalone='yes' ?>
<hierarchy index="0" class="hierarchy" rotation="0" width="1080" height="2400">
  <android.widget.FrameLayout index="0" package="com.example.chat" class="android.widget.FrameLayout" text="" content-desc="" resource-id="" bounds="[0,0][1080,2400]" displayed="true">
    <android.view.ViewGroup index="0" package="com.example.chat" class="android.view.ViewGroup" text="" content-desc="" resource-id="com.example.chat:id/message_list" bounds="[0,200][1080,2000]" displayed="true">
      <android.widget.TextView index="0" package="com.example.chat" class="android.widget.TextView" text="第一行&#10;第二行 &amp; &lt;标记&gt; &quot;引号&quot;" content-desc="" resource-id="" bounds="[40,220][1040,400]" displayed="true" />
      <android.widget.TextView index="1" package="com.example.chat" class="android.widget.TextView" text="合成时间" content-desc="" resource-id="com.example.chat:id/time_text" bounds="[40,420][1040,460]" displayed="true" />
    </android.view.ViewGroup>
    <android.view.View index="1" package="com.example.chat" class="android.view.View" text="" content-desc="开启新对话" resource-id="chat_new" bounds="[900,60][1040,180]" displayed="true" />
  </android.widget.FrameLayout>
</hierarchy>`;

describe("hierarchy export", () => {
  it("parses views with class, identifier, text, description and bounds in screen order", () => {
    const root = parseHierarchy(XML);
    const views = [...descendants(root)];
    expect(views.map((view) => view.className)).toEqual([
      "android.widget.FrameLayout",
      "android.view.ViewGroup",
      "android.widget.TextView",
      "android.widget.TextView",
      "android.view.View",
    ]);
    expect(views[2]!.text).toBe('第一行\n第二行 & <标记> "引号"');
    expect(views[1]).toMatchObject({
      resourceId: "com.example.chat:id/message_list",
      packageName: "com.example.chat",
      bounds: { left: 0, top: 200, right: 1080, bottom: 2000 },
    });
    expect(views[4]).toMatchObject({ resourceId: "chat_new", desc: "开启新对话" });
    expect(isInside(views[3]!, views[1]!)).toBe(true);
    expect(isInside(views[4]!, views[1]!)).toBe(false);
    expect(isReadable(root)).toBe(true);
  });
  it("treats an export without views, or with empty frames only, as not readable", () => {
    const empty = parseHierarchy('<hierarchy index="0" class="hierarchy"></hierarchy>');
    expect([...descendants(empty)]).toEqual([]);
    expect(isReadable(empty)).toBe(false);
    const frames = parseHierarchy(
      '<hierarchy rotation="0"><a.B text="" resource-id="" content-desc=""><a.C text="" /></a.B></hierarchy>',
    );
    expect([...descendants(frames)]).toHaveLength(2);
    expect(isReadable(frames)).toBe(false);
  });
  it("rejects text that is not a well-formed export", () => {
    for (const xml of ["", "not xml", "<hierarchy><a.B></hierarchy>", "<a.B /></a.B>"])
      expect(() => parseHierarchy(xml)).toThrowError("hierarchy_invalid");
  });
  it("parses bounds and refuses malformed ones", () => {
    expect(parseBounds("[0,66][1080,2337]")).toEqual({
      left: 0,
      top: 66,
      right: 1080,
      bottom: 2337,
    });
    expect(parseBounds("0,66,1080,2337")).toBeNull();
    expect(parseBounds(undefined)).toBeNull();
  });
});

describe("selectors", () => {
  const root = parseHierarchy(XML);
  it("requires every given condition", () => {
    // A package-prefixed identifier and a bare test tag are both matched as shown.
    expect(findNodes(root, { id: "com.example.chat:id/time_text" })).toHaveLength(1);
    expect(findNodes(root, { id: "time_text" })).toHaveLength(0);
    expect(findNodes(root, { id: "chat_new" })).toHaveLength(1);
    expect(findNodes(root, { text: "合成时间" })).toHaveLength(1);
    expect(findNodes(root, { textContains: "第二行" })).toHaveLength(1);
    expect(findNodes(root, { desc: "开启新对话" })).toHaveLength(1);
    expect(findNodes(root, { descContains: "新对话" })).toHaveLength(1);
    expect(findNodes(root, { className: "android.widget.TextView" })).toHaveLength(2);
    expect(
      findNodes(root, { className: "android.widget.TextView", textContains: "时间" }),
    ).toHaveLength(1);
    expect(findNodes(root, { className: "android.view.View", textContains: "时间" })).toHaveLength(
      0,
    );
  });
  it("needs at least one condition and knows no other keys", () => {
    expect(selectorSchema.safeParse({}).success).toBe(false);
    expect(selectorSchema.safeParse({ pick: "first" }).success).toBe(false);
    expect(selectorSchema.safeParse({ text: "" }).success).toBe(false);
    expect(selectorSchema.safeParse({ id: "chat_new", xpath: "//*" }).success).toBe(false);
    expect(selectorSchema.safeParse({ id: "chat_new", activity: ".Main" }).success).toBe(false);
    expect(selectorSchema.safeParse({ desc: "打开侧边栏", pick: "last" }).success).toBe(true);
  });
  it("matches a text pattern against the whole text, line breaks included", () => {
    expect(findNodes(root, { textMatches: "合成.*" })).toHaveLength(1);
    // Not a search inside the text: a pattern for a part of it does not match.
    expect(findNodes(root, { textMatches: "合成" })).toHaveLength(0);
    expect(findNodes(root, { textMatches: "时间" })).toHaveLength(0);
    expect(findNodes(root, { textMatches: ".*时间" })).toHaveLength(1);
    // An alternative is not anchored on one side only.
    expect(findNodes(root, { textMatches: "合成|时间" })).toHaveLength(0);
    // As on the device, "." also matches a line break.
    expect(findNodes(root, { textMatches: "第一行.*引号." })).toHaveLength(1);
    // Every other condition must hold as well.
    expect(
      findNodes(root, { textMatches: "合成.{2}", id: "com.example.chat:id/time_text" }),
    ).toHaveLength(1);
    expect(
      findNodes(root, { textMatches: "合成.{2}", className: "android.view.View" }),
    ).toHaveLength(0);
    const view = { resourceId: "", desc: "", className: "android.widget.TextView" };
    const tools = { textMatches: "使用 \\d+ 个工具.*" };
    expect(matchesSelector({ ...view, text: "使用 3 个工具，合成说明" }, tools)).toBe(true);
    expect(matchesSelector({ ...view, text: "使用 12 个工具" }, tools)).toBe(true);
    expect(matchesSelector({ ...view, text: "合成回答：使用 3 个工具，合成说明" }, tools)).toBe(
      false,
    );
    expect(matchesSelector({ ...view, text: "使用 多 个工具" }, tools)).toBe(false);
  });
  it("accepts a text pattern only if it compiles and can be sent to the device", () => {
    const accepts = (textMatches: string) => selectorSchema.safeParse({ textMatches }).success;
    expect(accepts("使用 \\d+ 个工具.*")).toBe(true);
    expect(accepts("搜索网页.*")).toBe(true);
    // A pattern alone is a condition.
    expect(selectorSchema.safeParse({ textMatches: "搜索网页.*", pick: "first" }).success).toBe(
      true,
    );
    expect(accepts("")).toBe(false);
    expect(accepts("(未闭合")).toBe(false);
    expect(accepts("[a-")).toBe(false);
    expect(accepts("a{2,1}")).toBe(false);
    // A backslash at the end would escape the quote that closes the pattern on the device.
    expect(accepts("合成\\\\")).toBe(false);
  });
  it("does not treat pick as a condition", () => {
    const view = [...descendants(root)][4]!;
    expect(matchesSelector(view, { id: "chat_new", pick: "last" })).toBe(true);
  });
});

// A screen an App draws itself (design §16.8): no resource IDs, texts as content descriptions,
// controls as image views without any label. Every value is synthetic.
const view = (className: string, attributes: string, bounds: string) =>
  `<${className} class="${className}" text="" resource-id="" ${attributes} bounds="${bounds}" />`;
const icon = (bounds: string, attributes = 'content-desc="" clickable="true"') =>
  view("android.widget.ImageView", attributes, bounds);
const DRAWN = `<hierarchy rotation="0">
  ${icon("[40,150][140,250]")}
  ${icon("[190,170][610,240]", 'content-desc="合成模型&#10;快速" clickable="true"')}
  ${icon("[830,176][890,239]")}
  ${icon("[930,176][990,239]")}
  ${view("android.view.View", 'content-desc="合成回答，其中提到这3个来源" clickable="true"', "[40,300][1040,900]")}
  ${icon("[840,910][910,980]")}
  ${icon("[930,910][1000,980]")}
  ${view("android.view.View", 'content-desc="任务" clickable="false"', "[40,1000][400,1100]")}
  ${view("android.view.View", 'content-desc="30个来源" clickable="true"', "[40,1250][250,1320]")}
  ${icon("[226,1353][273,1412]")}
  ${icon("[42,1353][89,1412]")}
  ${icon("[131,1353][184,1412]")}
  ${icon("[60,1500][100,1540]", 'content-desc="" clickable="false"')}
  ${view("android.view.View", 'content-desc="任务" clickable="true"', "[340,2128][494,2203]")}
  ${icon("[66,2125][150,2209]")}
  <android.widget.EditText class="android.widget.EditText" text="" resource-id="" content-desc="" clickable="true" bounds="[170,2120][910,2215]" />
  ${icon("[930,2125][1014,2209]")}
  <android.widget.ImageView class="android.widget.ImageView" text="" resource-id="com.example.chat:id/avatar" content-desc="" clickable="true" bounds="[50,2250][110,2310]" />
</hierarchy>`;

describe("selectors for views without a label", () => {
  const root = parseHierarchy(DRAWN);
  const image = { className: "android.widget.ImageView" };
  const lefts = (selector: Parameters<typeof findNodes>[1]) =>
    findNodes(root, selector).map((node) => node.bounds!.left);

  it("matches a description pattern against the whole description", () => {
    expect(findNodes(root, { descMatches: "[0-9]+个来源" })).toHaveLength(1);
    // A sentence of the answer that holds the same words is not the source entry.
    expect(findNodes(root, { descContains: "个来源" })).toHaveLength(2);
    expect(findNodes(root, { descMatches: "合成模型.快速" })).toHaveLength(1);
    expect(selectorSchema.safeParse({ descMatches: "(未闭合" }).success).toBe(false);
    expect(selectorSchema.safeParse({ descMatches: "合成\\\\" }).success).toBe(false);
  });
  it("tells a view that reacts to a tap from one that only shows the same words", () => {
    expect(findNodes(root, { desc: "任务" })).toHaveLength(2);
    expect(findNodes(root, { desc: "任务", clickable: true })).toHaveLength(1);
    expect(findNodes(root, { desc: "任务", clickable: false })[0]!.bounds!.top).toBe(1000);
    // A view that does not say whether it reacts to a tap meets neither.
    const plain = { resourceId: "", text: "", desc: "任务", className: "android.view.View" };
    expect(matchesSelector(plain, { desc: "任务" })).toBe(true);
    expect(matchesSelector(plain, { desc: "任务", clickable: true })).toBe(false);
    expect(matchesSelector(plain, { desc: "任务", clickable: false })).toBe(false);
  });
  it("takes a view for unlabeled only without text, description and resource ID", () => {
    // Not the model name (a description), not the avatar (a resource ID).
    expect(findNodes(root, image)).toHaveLength(13);
    expect(findNodes(root, { ...image, unlabeled: true })).toHaveLength(11);
    expect(findNodes(root, { ...image, unlabeled: true, clickable: true })).toHaveLength(10);
    expect(findNodes(root, { className: "android.widget.EditText", unlabeled: true })).toHaveLength(
      1,
    );
    const filled = { resourceId: "", text: "合成", desc: "", className: "android.widget.EditText" };
    expect(matchesSelector(filled, { className: filled.className, unlabeled: true })).toBe(false);
  });
  it("finds the n-th view from the left of a row of exactly that many like views", () => {
    const bare = { ...image, unlabeled: true as const, clickable: true };
    // The row of three below the answer, in whatever order the export lists its views.
    expect(lefts({ ...bare, row: { size: 3, at: 0 } })).toEqual([42]);
    expect(lefts({ ...bare, row: { size: 3, at: 1 } })).toEqual([131]);
    expect(lefts({ ...bare, row: { size: 3, at: 2 } })).toEqual([226]);
    // Three rows of two, in screen order: the top bar, a table's icons, the composer.
    expect(lefts({ ...bare, row: { size: 2, at: 0 } })).toEqual([830, 840, 66]);
    expect(lefts({ ...bare, row: { size: 2, at: 1 } })).toEqual([930, 930, 930]);
    // No row of four, and the menu icon stands alone.
    expect(lefts({ ...bare, row: { size: 4, at: 0 } })).toEqual([]);
    // The other conditions decide who counts: with the avatar, which has a resource ID, and
    // the icon that takes no tap, the rows are the same, since neither shares their edges.
    expect(lefts({ ...image, row: { size: 3, at: 0 } })).toEqual([42]);
  });
  it("does not call a row what has one view more or fewer", () => {
    const row = (count: number) =>
      Array.from({ length: count }, (_, index) => ({
        bounds: { left: index * 100, top: 0, right: index * 100 + 50, bottom: 50 },
      }));
    for (const count of [1, 2, 4, 5])
      expect(inRow(row(count), { row: { size: 3, at: 0 } })).toEqual([]);
    expect(inRow(row(3), { row: { size: 3, at: 0 } })).toHaveLength(1);
    // Same top, another bottom: not one row. A view without bounds is in no row.
    const uneven = [...row(2), { bounds: { left: 200, top: 0, right: 250, bottom: 60 } }];
    expect(inRow(uneven, { row: { size: 3, at: 0 } })).toEqual([]);
    expect(inRow([...row(2), { bounds: null }], { row: { size: 3, at: 0 } })).toEqual([]);
    expect(inRow([...row(2), { bounds: null }], { row: { size: 2, at: 1 } })).toHaveLength(1);
    // Views that take up no room are not on the screen: three of them that report the same
    // empty box (what is left of views scrolled out of their list) are not a row of three.
    const empty = (left: number) => ({ bounds: { left, top: 0, right: left, bottom: 0 } });
    expect(inRow([empty(0), empty(0), empty(0)], { row: { size: 3, at: 0 } })).toEqual([]);
    const flat = (left: number) => ({ bounds: { left, top: 900, right: left + 50, bottom: 900 } });
    expect(inRow([flat(0), flat(100), flat(200)], { row: { size: 3, at: 0 } })).toEqual([]);
    // Without the condition nothing is filtered.
    expect(inRow(row(4), {})).toHaveLength(4);
  });
  it("accepts unlabeled and row only with a class, and unlabeled with no label condition", () => {
    const accepts = (value: unknown) => selectorSchema.safeParse(value).success;
    expect(accepts({ ...image, unlabeled: true })).toBe(true);
    expect(accepts({ ...image, unlabeled: true, clickable: true, row: { size: 3, at: 0 } })).toBe(
      true,
    );
    expect(accepts({ ...image, row: { size: 2, at: 1 }, pick: "first" })).toBe(true);
    expect(accepts({ unlabeled: true })).toBe(false);
    expect(accepts({ desc: "任务", row: { size: 2, at: 0 } })).toBe(false);
    expect(accepts({ ...image, unlabeled: false })).toBe(false);
    for (const label of [
      { id: "chat_new" },
      { text: "合成" },
      { textContains: "合成" },
      { textMatches: "合成.*" },
      { desc: "合成" },
      { descContains: "合成" },
      { descMatches: "合成.*" },
    ])
      expect(accepts({ ...image, unlabeled: true, ...label })).toBe(false);
    // Whether a view takes a tap narrows a selector and is never the only condition (design
    // §16.8): alone it would name every view of a screen that does, or every one that does not.
    expect(accepts({ clickable: true })).toBe(false);
    expect(accepts({ clickable: false })).toBe(false);
    expect(accepts({ clickable: true, pick: "first" })).toBe(false);
    expect(accepts({ ...image, clickable: false })).toBe(true);
    expect(viewSelectorSchema.safeParse({ clickable: true }).success).toBe(false);
    // A row needs two views at least and a place inside it.
    expect(accepts({ ...image, row: { size: 1, at: 0 } })).toBe(false);
    expect(accepts({ ...image, row: { size: 7, at: 0 } })).toBe(false);
    expect(accepts({ ...image, row: { size: 3, at: 3 } })).toBe(false);
    expect(accepts({ ...image, row: { size: 3, at: -1 } })).toBe(false);
    expect(accepts({ ...image, row: { size: 3 } })).toBe(false);
    expect(accepts({ ...image, row: { size: 3, at: 0, of: "siblings" } })).toBe(false);
  });
  it("refuses a row where views are compared one by one", () => {
    expect(viewSelectorSchema.safeParse({ ...image, unlabeled: true }).success).toBe(true);
    expect(viewSelectorSchema.safeParse({ ...image, row: { size: 3, at: 0 } }).success).toBe(false);
    expect(viewSelectorSchema.safeParse({}).success).toBe(false);
    // Asked about one view, a row condition is left to `findNodes`.
    const first = findNodes(root, image)[0]!;
    expect(matchesSelector(first, { ...image, row: { size: 3, at: 0 } })).toBe(true);
  });
});

describe("joining reads of a scrolling list", () => {
  it("drops the repeated part once", () => {
    expect(mergeOverlap(["a", "b", "c"], ["b", "c", "d"])).toEqual(["a", "b", "c", "d"]);
    expect(mergeOverlap(["a"], ["a", "b"])).toEqual(["a", "b"]);
    expect(mergeOverlap(["a", "b"], ["a", "b"])).toEqual(["a", "b"]);
    // The longest repetition wins, so a repeated line is not mistaken for the whole overlap.
    expect(mergeOverlap(["x", "a", "x", "a"], ["x", "a", "y"])).toEqual(["x", "a", "x", "a", "y"]);
  });
  it("keeps everything when nothing repeats or one side is empty", () => {
    expect(mergeOverlap(["a"], ["b"])).toEqual(["a", "b"]);
    expect(mergeOverlap([], ["b"])).toEqual(["b"]);
    expect(mergeOverlap(["a"], [])).toEqual(["a"]);
  });
  it("compares items with the given equality", () => {
    const same = (a: { t: string }, b: { t: string }) => a.t === b.t;
    expect(mergeOverlap([{ t: "a" }, { t: "b" }], [{ t: "b" }, { t: "c" }], same)).toEqual([
      { t: "a" },
      { t: "b" },
      { t: "c" },
    ]);
  });
  it("tells a screen that changed from one that did not, by content and by position", () => {
    const same = `${XML}`;
    expect(layoutSignature(parseHierarchy(same))).toBe(layoutSignature(parseHierarchy(XML)));
    // The same views and texts somewhere else: the content moved.
    const moved = XML.replace("[40,420][1040,460]", "[40,300][1040,340]");
    expect(layoutSignature(parseHierarchy(moved))).not.toBe(layoutSignature(parseHierarchy(XML)));
    expect(layoutSignature(parseHierarchy(XML.replace("合成时间", "合成时间二")))).not.toBe(
      layoutSignature(parseHierarchy(XML)),
    );
    expect(layoutSignature(parseHierarchy(XML.replace("开启新对话", "开启")))).not.toBe(
      layoutSignature(parseHierarchy(XML)),
    );
  });

  it("joins source windows at the last common item when a window carries a misread edge", () => {
    // Exact overlap, as with mergeOverlap.
    expect(mergeWindows([1, 2, 3], [2, 3, 4])).toEqual([1, 2, 3, 4]);
    expect(mergeWindows([1, 2, 3], [1, 2, 3, 4, 5, 6])).toEqual([1, 2, 3, 4, 5, 6]);
    // A misread card at the top of the later window is dropped instead of breaking the join.
    expect(mergeWindows([1, 2, 3], [99, 1, 2, 3, 4, 5, 6])).toEqual([1, 2, 3, 4, 5, 6]);
    expect(mergeWindows([1, 2, 3, 4, 5, 6], [99, 4, 5, 6, 7, 8])).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
    // A misread card at the end of the earlier list is replaced by what the later window shows.
    expect(mergeWindows([1, 2, 3, 98], [2, 3, 4, 5])).toEqual([1, 2, 3, 4, 5]);
    // Equal neighbours stay two items when the windows overlap exactly.
    expect(mergeWindows([1, 7, 7], [7, 7, 8])).toEqual([1, 7, 7, 8]);
    // Nothing in common: the later window follows the earlier one.
    expect(mergeWindows([1, 2], [5, 6])).toEqual([1, 2, 5, 6]);
    expect(mergeWindows([], [5, 6])).toEqual([5, 6]);
  });
  it("joins only where the items before the join agree as well", () => {
    // The join item shows again further down the later window (the same source listed twice):
    // what lies between is not dropped.
    expect(mergeWindows([1, 2, 3, 4, 5], [99, 4, 5, 6, 5])).toEqual([1, 2, 3, 4, 5, 6, 5]);
    expect(mergeWindows([1, 2, 3], [99, 2, 3, 3])).toEqual([1, 2, 3, 3]);
    expect(mergeWindows([1, 2, 3, 3], [99, 3, 3, 5])).toEqual([1, 2, 3, 3, 5]);
    // Equal neighbours behind a misread card: the longest agreement wins, as with an exact overlap.
    expect(mergeWindows([1, 7, 7], [99, 7, 7, 8])).toEqual([1, 7, 7, 8]);
    // A later window that only repeats an item from the middle of the earlier list is not joined
    // there: nothing the earlier list holds is thrown away for it.
    expect(mergeWindows([1, 2, 3, 4, 5], [99, 6, 2, 7])).toEqual([1, 2, 3, 4, 5, 99, 6, 2, 7]);
  });
});
