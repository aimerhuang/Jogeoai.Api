import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { MAX_SWIPES_PER_CALL } from "./driver.ts";
import { descendants, findNodes, parseHierarchy } from "./hierarchy.ts";
import {
  MOCK_FOREIGN_PACKAGE,
  MOCK_PACKAGE,
  MOCK_SYSTEM_SHARE_PACKAGE,
  MockChatApp,
  MockDriver,
  MockDrivers,
  MockIconApp,
} from "./mock-driver.ts";
import { DEVICE_A, steppingClock } from "./test-fixtures.ts";

const created: string[] = [];
afterEach(async () => {
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});
const id = (name: string) => ({ id: `${MOCK_PACKAGE}:id/${name}` });

// The mock is what every flow test runs on, so its own behaviour is pinned down here.
describe("mock driver", () => {
  function chat(options: Omit<ConstructorParameters<typeof MockChatApp>[0], "now"> = {}) {
    const clock = steppingClock();
    const app = new MockChatApp({ now: clock.now, ...options });
    return { clock, app, driver: new MockDriver(app) };
  }
  // Drives the mock App by hand to a conversation with a sent text.
  async function ask(driver: MockDriver, text: string) {
    await driver.activateApp(MOCK_PACKAGE);
    await driver.tap((await driver.find(id("action_input")))[0]!);
    await driver.setText((await driver.find(id("input_text")))[0]!, text);
    await driver.tap((await driver.find(id("action_send")))[0]!);
  }

  it("shows the launcher until the App is activated, and again after home", async () => {
    const { driver } = chat();
    expect(await driver.foregroundPackage()).toBe("com.android.launcher3");
    expect(await driver.find(id("back_icon"))).toEqual([]);
    await driver.activateApp(MOCK_PACKAGE);
    expect(await driver.foregroundPackage()).toBe(MOCK_PACKAGE);
    expect(await driver.find(id("larus_mode_switch_welcome_title"))).toHaveLength(1);
    await driver.home();
    expect(await driver.foregroundPackage()).toBe("com.android.launcher3");
  });
  it("exports the same views a lookup finds, in the real hierarchy format", async () => {
    const { driver } = chat();
    await ask(driver, '合成 "问题" <一> & 二\n第二行');
    const root = parseHierarchy(await driver.hierarchy());
    const list = findNodes(root, id("message_list"));
    expect(list).toHaveLength(1);
    // Text survives escaping, and nesting is kept.
    expect([...descendants(list[0]!)].map((node) => node.text)).toEqual([
      '合成 "问题" <一> & 二\n第二行',
      "合成回答第一段",
    ]);
    expect(list[0]!.packageName).toBe(MOCK_PACKAGE);
    expect(findNodes(root, id("back_icon"))[0]!.bounds).toEqual({
      left: 0,
      top: 0,
      right: 1080,
      bottom: 100,
    });
    const found = await driver.find({ className: "android.widget.TextView" });
    expect(found.map((view) => view.text)).toEqual(
      findNodes(root, { className: "android.widget.TextView" }).map((node) => node.text),
    );
  });
  it("completes the answer only after the generation time and does not follow it", async () => {
    const { driver, clock } = chat({
      answer: ["一", "二", "三", "四", "五"],
      viewport: 3,
      generationMs: 20_000,
    });
    await ask(driver, "合成问题");
    expect(await driver.find(id("msg_action_regenerate"))).toEqual([]);
    clock.advance(20_000);
    // Complete, but the end is below the screen until the jump-to-end button is used.
    expect(await driver.find(id("msg_action_regenerate"))).toEqual([]);
    await driver.tap((await driver.find(id("fast_button_icon")))[0]!);
    expect(await driver.find(id("msg_action_regenerate"))).toHaveLength(1);
    expect(await driver.find(id("fast_button_icon"))).toEqual([]);
    // Swiping down brings earlier rows back.
    await driver.swipe("down", 4);
    expect(await driver.find(id("msg_action_regenerate"))).toEqual([]);
    expect(await driver.find(id("fast_button_icon"))).toHaveLength(1);
  });
  it("fails a command on a view that is no longer on the screen, and refuses unbounded swipes", async () => {
    const { driver } = chat();
    await driver.activateApp(MOCK_PACKAGE);
    const [toggle] = await driver.find(id("action_input"));
    await driver.tap(toggle!);
    await expect(driver.tap(toggle!)).rejects.toMatchObject({ kind: "stale_element" });
    await expect(
      driver.setText((await driver.find(id("back_icon")))[0]!, "x"),
    ).rejects.toMatchObject({ kind: "command_failed" });
    for (const count of [0, MAX_SWIPES_PER_CALL + 1])
      await expect(driver.swipe("up", count)).rejects.toThrow(RangeError);
  });
  it("returns empty exports first when told to, writes a screenshot and records its calls", async () => {
    const directory = await mkdtemp(join(tmpdir(), "aivc-mock-driver-"));
    created.push(directory);
    const clock = steppingClock();
    const driver = new MockDriver(new MockChatApp({ now: clock.now }), { emptyReads: 1 });
    await driver.activateApp(MOCK_PACKAGE);
    expect([...descendants(parseHierarchy(await driver.hierarchy()))]).toEqual([]);
    expect([...descendants(parseHierarchy(await driver.hierarchy()))].length).toBeGreaterThan(0);
    await driver.screenshot(join(directory, "final.png"));
    expect(await readFile(join(directory, "final.png"), "utf8")).toBe("synthetic png bytes");
    await driver.close();
    expect(driver.calls).toEqual(["activateApp", "hierarchy", "hierarchy", "screenshot", "close"]);
    expect(driver.closed).toBe(true);
  });
  it("shows the tool steps of an answer, their source lists and the way back", async () => {
    const { driver, clock } = chat({
      generationMs: 1_000,
      toolSteps: [
        { text: "搜索网页 合成关键词", sources: [{ title: "合成来源一", site: "合成站点一" }] },
        { text: "获取数据 合成数据库" },
      ],
    });
    await ask(driver, "合成问题");
    const line = { textMatches: "使用 \\d+ 个工具.*" };
    // The line belongs to the finished answer.
    expect(await driver.find(line)).toEqual([]);
    clock.advance(1_000);
    await driver.tap((await driver.find(line))[0]!);
    // Every lookup is kept with the selector it was made with.
    expect(driver.lookups.slice(-2)).toEqual([line, line]);
    expect(await driver.find({ text: "工具调用" })).toHaveLength(1);
    // A step that is no search leads nowhere.
    await driver.tap((await driver.find({ text: "获取数据 合成数据库" }))[0]!);
    expect(await driver.find({ text: "工具调用" })).toHaveLength(1);
    await driver.tap((await driver.find({ text: "搜索网页 合成关键词" }))[0]!);
    expect(await driver.find({ text: "网页搜索" })).toHaveLength(1);
    expect(await driver.find({ text: "工具调用" })).toEqual([]);
    // A card is as tall as its own bounds say, whatever it holds.
    const root = parseHierarchy(await driver.hierarchy());
    const card = findNodes(root, { text: "合成来源一" })[0]!.parent!;
    expect(card.clickable).toBe(true);
    expect(card.bounds!.bottom - card.bounds!.top).toBe(160);
    await driver.back();
    expect(await driver.find({ text: "工具调用" })).toHaveLength(1);
    await driver.back();
    expect(await driver.find(id("message_list"))).toHaveLength(1);
    expect(await driver.foregroundPackage()).toBe(MOCK_PACKAGE);
  });
  // Task 11-24: the page a source's title opens, its share sheet and the way back.
  it("opens a source's page from its title, copies its link and goes back to the panel as it was", async () => {
    const { driver, clock } = chat({
      generationMs: 1_000,
      sourcePages: true,
      sourcesViewport: 2,
      shareSlideMs: 800,
      linkOverlayMs: 3_000,
      citations: [
        { title: "合成来源一", link: "https://example.com/1" },
        { title: "合成来源二", link: "https://example.com/2", deadTaps: 1 },
        { title: "合成来源三", noShare: true },
        { title: "合成来源四", link: "https://example.com/4", copyMisses: 1 },
      ],
    });
    const title = (text: string) => ({ ...id("tv_reference_content"), text });
    await ask(driver, "合成问题");
    clock.advance(1_000);
    await driver.tap((await driver.find(id("ll_reference_title")))[0]!);
    await driver.tap((await driver.find(title("合成来源一")))[0]!);
    expect(await driver.find(id("bullet_container"))).toHaveLength(1);
    expect(await driver.find(id("bottom_sheet_title"))).toEqual([]);
    // The top bar: close and share, the page's title; no address anywhere.
    const page = parseHierarchy(await driver.hierarchy());
    expect(findNodes(page, id("btn_back"))[0]!.desc).toBe("关闭");
    expect(findNodes(page, id("tv_title"))[0]!.text).toBe("合成来源一");
    expect(JSON.stringify([...descendants(page)].map((node) => node.text))).not.toContain(
      "example.com",
    );
    await driver.tap((await driver.find(id("btn_share")))[0]!);
    // While the sheet slides in, it moves from look to look and "copy link" does nothing.
    const sheet = async () =>
      JSON.stringify(
        [...descendants(parseHierarchy(await driver.hierarchy()))].map((node) => node.bounds),
      );
    const first = await sheet();
    clock.advance(100);
    expect(await sheet()).not.toBe(first);
    const copy = (await driver.find({ text: "复制链接" }))[0]!;
    await driver.tap(copy);
    expect(await driver.readClipboard()).toBe("");
    clock.advance(800);
    expect(await sheet()).toBe(await sheet());
    await driver.tap((await driver.find({ text: "复制链接" }))[0]!);
    expect(await driver.readClipboard()).toBe("https://example.com/1");
    // The copy closes the sheet; the preview then hides the panel's rows for a while.
    expect(await driver.find({ text: "复制链接" })).toEqual([]);
    await driver.back();
    expect(await driver.find(id("bottom_sheet_title"))).toHaveLength(1);
    expect(await driver.find(id("tv_reference_content"))).toEqual([]);
    clock.advance(3_000);
    expect((await driver.find(id("tv_reference_content"))).map((view) => view.text)).toEqual([
      "合成来源一",
      "合成来源二",
    ]);
    // A title that does not react the first time.
    await driver.tap((await driver.find(title("合成来源二")))[0]!);
    expect(await driver.find(id("bullet_container"))).toEqual([]);
    await driver.tap((await driver.find(title("合成来源二")))[0]!);
    expect(await driver.find(id("bullet_container"))).toHaveLength(1);
    // The back key closes the sheet first, then the page; the list is where it was left.
    await driver.tap((await driver.find(id("btn_share")))[0]!);
    await driver.back();
    expect(await driver.find(id("bullet_container"))).toHaveLength(1);
    await driver.back();
    // A swipe moves a list of two rows by one.
    await driver.swipe("up", 2);
    expect((await driver.find(id("tv_reference_content"))).map((view) => view.text)).toEqual([
      "合成来源三",
      "合成来源四",
    ]);
    // A page without a share control.
    await driver.tap((await driver.find(title("合成来源三")))[0]!);
    expect(await driver.find(id("bullet_container"))).toHaveLength(1);
    expect(await driver.find(id("btn_share"))).toEqual([]);
    await driver.back();
    // A copy that misses leaves the sheet and the clipboard as they were.
    await driver.tap((await driver.find(title("合成来源四")))[0]!);
    await driver.tap((await driver.find(id("btn_share")))[0]!);
    clock.advance(800);
    await driver.tap((await driver.find({ text: "复制链接" }))[0]!);
    expect(await driver.readClipboard()).toBe("https://example.com/1");
    await driver.tap((await driver.find({ text: "复制链接" }))[0]!);
    expect(await driver.readClipboard()).toBe("https://example.com/4");
  });
  // Task 11-24 §11: Android's own share sheet, another App, and a preview over the last rows.
  // Task 11-24 §13: the preview after "copy link" swallows swipes on the list for a while.
  it("does not move the source list while the preview after a copy swallows swipes", async () => {
    const { driver, clock } = chat({
      generationMs: 0,
      sourcePages: true,
      sourcesViewport: 2,
      linkSwipeSwallowMs: 8_000,
      citations: [
        { title: "合成来源一", link: "https://example.com/1" },
        { title: "合成来源二", link: "https://example.com/2" },
        { title: "合成来源三", link: "https://example.com/3" },
      ],
    });
    const titles = async () =>
      (await driver.find(id("tv_reference_content"))).map((view) => view.text);
    await ask(driver, "合成问题");
    await driver.tap((await driver.find(id("ll_reference_title")))[0]!);
    // Before any copy a swipe moves the list.
    await driver.swipe("up", 1);
    expect(await titles()).toEqual(["合成来源二", "合成来源三"]);
    await driver.swipe("down", 1);
    await driver.tap(
      (await driver.find({ ...id("tv_reference_content"), text: "合成来源一" }))[0]!,
    );
    await driver.tap((await driver.find(id("btn_share")))[0]!);
    await driver.tap((await driver.find({ text: "复制链接" }))[0]!);
    await driver.back();
    // Swallowed for its time, whatever the rows show.
    await driver.swipe("up", 1);
    expect(await titles()).toEqual(["合成来源一", "合成来源二"]);
    clock.advance(8_000);
    await driver.swipe("up", 1);
    expect(await titles()).toEqual(["合成来源二", "合成来源三"]);
  });
  it("opens Android's share sheet or another App from a source, and hides the last rows under a preview", async () => {
    const { driver, clock } = chat({
      generationMs: 0,
      sourcePages: true,
      sourcesViewport: 3,
      linkOverlayMs: 5_000,
      linkOverlayRows: 2,
      citations: [
        { title: "合成来源一", link: "https://example.com/1", systemShare: "dismisses" },
        { title: "合成来源二", link: "https://example.com/2", systemShare: "stays" },
        { title: "合成来源三", opensApp: "returns" },
        { title: "合成来源四", opensApp: "stays" },
      ],
    });
    const title = (text: string) => ({ ...id("tv_reference_content"), text });
    const copy = { id: `${MOCK_SYSTEM_SHARE_PACKAGE}:id/copy` };
    const titles = async () =>
      (await driver.find(id("tv_reference_content"))).map((view) => view.text);
    await ask(driver, "合成问题");
    await driver.tap((await driver.find(id("ll_reference_title")))[0]!);
    // Android's sheet is in its own package, over the page; its copy closes it.
    await driver.tap((await driver.find(title("合成来源一")))[0]!);
    await driver.tap((await driver.find(id("btn_share")))[0]!);
    expect(await driver.foregroundPackage()).toBe(MOCK_SYSTEM_SHARE_PACKAGE);
    expect(await driver.find({ id: "android:id/content_preview_text" })).toHaveLength(1);
    await driver.tap((await driver.find(copy))[0]!);
    expect(await driver.readClipboard()).toBe("https://example.com/1");
    expect(await driver.foregroundPackage()).toBe(MOCK_PACKAGE);
    expect(await driver.find(id("bullet_container"))).toHaveLength(1);
    // The preview hides the last two rows of the three on screen, for its time only.
    await driver.back();
    expect(await titles()).toEqual(["合成来源一"]);
    clock.advance(5_000);
    expect(await titles()).toEqual(["合成来源一", "合成来源二", "合成来源三"]);
    // A sheet that stays after its copy, until the back key.
    await driver.tap((await driver.find(title("合成来源二")))[0]!);
    await driver.tap((await driver.find(id("btn_share")))[0]!);
    await driver.tap((await driver.find(copy))[0]!);
    expect(await driver.readClipboard()).toBe("https://example.com/2");
    expect(await driver.foregroundPackage()).toBe(MOCK_SYSTEM_SHARE_PACKAGE);
    await driver.back();
    expect(await driver.foregroundPackage()).toBe(MOCK_PACKAGE);
    await driver.back();
    clock.advance(5_000);
    // A source that opens another App; the back key leads to the panel.
    await driver.tap((await driver.find(title("合成来源三")))[0]!);
    expect(await driver.foregroundPackage()).toBe(MOCK_FOREIGN_PACKAGE);
    expect(await driver.find(id("bullet_container"))).toEqual([]);
    await driver.back();
    expect(await driver.find(id("bottom_sheet_title"))).toHaveLength(1);
    // One whose App does not give way.
    await driver.swipe("up", 1);
    await driver.tap((await driver.find(title("合成来源四")))[0]!);
    await driver.back();
    await driver.back();
    expect(await driver.foregroundPackage()).toBe(MOCK_FOREIGN_PACKAGE);
  });
  it("can keep a source's page open, and show a blocker on it", async () => {
    const stuck = chat({
      generationMs: 0,
      sourcePages: true,
      pageStuck: true,
      citations: [{ title: "合成来源一" }],
    });
    await ask(stuck.driver, "合成问题");
    await stuck.driver.tap((await stuck.driver.find(id("ll_reference_title")))[0]!);
    await stuck.driver.tap((await stuck.driver.find(id("tv_reference_content")))[0]!);
    await stuck.driver.back();
    expect(await stuck.driver.find(id("bullet_container"))).toHaveLength(1);

    const blocked = chat({
      generationMs: 0,
      sourcePages: true,
      blocker: { code: "challenge_required", when: "source_page" },
      citations: [{ title: "合成来源一" }],
    });
    await ask(blocked.driver, "合成问题");
    await blocked.driver.tap((await blocked.driver.find(id("ll_reference_title")))[0]!);
    expect(await blocked.driver.find(id("mock_verification"))).toEqual([]);
    await blocked.driver.tap((await blocked.driver.find(id("tv_reference_content")))[0]!);
    expect(await blocked.driver.find(id("mock_verification"))).toHaveLength(1);
    expect(await blocked.driver.find(id("bullet_container"))).toEqual([]);
  });
  it("shows an answer with two source entries: the tool steps at its start, the panel's at its end", async () => {
    const options = {
      generationMs: 1_000,
      sourceEntryAtEnd: true,
      citations: [{ title: "合成来源一" }],
      toolSteps: [{ text: "搜索网页 合成关键词", sources: [{ title: "合成来源二" }] }],
    };
    const { driver, clock } = chat(options);
    await ask(driver, "合成问题");
    clock.advance(1_000);
    const order = [...descendants(parseHierarchy(await driver.hierarchy()))]
      .map((node) => node.resourceId.replace(`${MOCK_PACKAGE}:id/`, ""))
      .filter((name) =>
        ["mock_tool_summary", "ll_reference_title", "msg_action_copy"].includes(name),
      );
    // The answer text sits between the two entries.
    expect(order).toEqual(["mock_tool_summary", "ll_reference_title", "msg_action_copy"]);
    const texts = [...descendants(parseHierarchy(await driver.hierarchy()))].map(
      (node) => node.text,
    );
    expect(texts.indexOf("合成回答第二段")).toBeLessThan(
      texts.indexOf("搜索关键词，参考 1 篇资料"),
    );
    // Each entry opens its own screen.
    await driver.tap((await driver.find(id("ll_reference_title")))[0]!);
    expect(await driver.find(id("bottom_sheet_title"))).toHaveLength(1);
    await driver.tap((await driver.find(id("bottom_sheet_close")))[0]!);
    await driver.tap((await driver.find(id("mock_tool_summary")))[0]!);
    expect(await driver.find({ text: "工具调用" })).toHaveLength(1);

    // With a dead panel entry, the entry of the tool steps still opens.
    const dead = chat({ ...options, panelEntryDead: true });
    await ask(dead.driver, "合成问题");
    dead.clock.advance(1_000);
    await dead.driver.tap((await dead.driver.find(id("ll_reference_title")))[0]!);
    expect(await dead.driver.find(id("bottom_sheet_title"))).toEqual([]);
    await dead.driver.tap((await dead.driver.find(id("mock_tool_summary")))[0]!);
    expect(await dead.driver.find({ text: "工具调用" })).toHaveLength(1);
  });
  it("finds the icons of an App without labels by their row, as the hierarchy export does", async () => {
    const clock = steppingClock();
    const app = new MockIconApp({ now: clock.now, startInConversation: true });
    const driver = new MockDriver(app);
    await driver.activateApp(MOCK_PACKAGE);
    const icon = { className: "android.widget.ImageView", unlabeled: true as const };
    const root = parseHierarchy(await driver.hierarchy());
    for (const selector of [
      icon,
      { ...icon, clickable: true },
      { ...icon, clickable: true, row: { size: 2, at: 0 } },
      { ...icon, clickable: true, row: { size: 2, at: 1 } },
      { ...icon, clickable: true, row: { size: 3, at: 0 } },
      { ...icon, clickable: true, row: { size: 4, at: 0 } },
      { desc: "任务", clickable: true },
      { desc: "任务", clickable: false },
      { descMatches: "合成模型.快速" },
    ])
      expect((await driver.find(selector)).map((view) => view.bounds)).toEqual(
        findNodes(root, selector).map((node) => node.bounds),
      );
    // Three rows of two (top bar, a table's icons, the composer) and one row of three.
    expect(await driver.find({ ...icon, clickable: true, row: { size: 2, at: 0 } })).toHaveLength(
      3,
    );
    expect(await driver.find({ ...icon, clickable: true, row: { size: 3, at: 0 } })).toHaveLength(
      1,
    );
  });
  it("fails a tap on a view without a label that is no longer where it was found", async () => {
    const clock = steppingClock();
    const app = new MockIconApp({ now: clock.now, startInConversation: true });
    const driver = new MockDriver(app);
    await driver.activateApp(MOCK_PACKAGE);
    const icon = { className: "android.widget.ImageView", unlabeled: true as const };
    // The pair of icons of a conversation's top bar: "new conversation" and "more".
    const [fresh] = await driver.find({ ...icon, clickable: true, row: { size: 2, at: 0 } });
    const [more] = await driver.find({ ...icon, clickable: true, row: { size: 2, at: 1 } });
    await driver.tap(fresh!);
    // The home screen has no view where "more" was. A view without a label is known by its
    // place only, so the tap does not fall to another icon that looks the same (the menu).
    await expect(driver.tap(more!)).rejects.toMatchObject({ kind: "stale_element" });
    expect(await driver.find(id("iv_sidebar_close"))).toEqual([]);
    expect(app.strayTaps).toEqual([]);
  });
  it("opens one session per call for a known device only", async () => {
    const clock = steppingClock();
    const drivers = new MockDrivers({ [DEVICE_A]: new MockChatApp({ now: clock.now }) });
    const first = await drivers.open({ deviceId: DEVICE_A, serial: "emulator-5556" });
    const second = await drivers.open({ deviceId: DEVICE_A, serial: "emulator-5556" });
    expect(first).not.toBe(second);
    await expect(drivers.open({ deviceId: "other", serial: "emulator-1" })).rejects.toMatchObject({
      kind: "unreachable",
    });
  });
});
