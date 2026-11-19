import { lstat, mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import {
  AppiumDriver,
  AppiumDrivers,
  sessionCapabilities,
  toUiSelector,
  type AppiumClient,
} from "./appium-driver.ts";
import {
  APPIUM_BIN,
  APPIUM_HELPER_PACKAGES,
  APPIUM_HOME,
  AppiumServer,
  readDriverTools,
} from "./appium-server.ts";
import type { CommandRunner } from "./command.ts";
import { MAX_SWIPES_PER_CALL } from "./driver.ts";

const created: string[] = [];
afterEach(async () => {
  for (const directory of created.splice(0)) await rm(directory, { recursive: true, force: true });
});

const ELEMENT = "element-6066-11e4-a52e-4f735466cecf";
const XML =
  '<hierarchy rotation="0"><android.widget.TextView text="合成" resource-id="a:id/b" bounds="[0,0][10,10]" /></hierarchy>';

// Records every protocol command; nothing here talks to a server or a device.
function fakeClient(overrides: Partial<AppiumClient> = {}) {
  const calls: unknown[][] = [];
  const attributes: Record<string, string> = {
    text: "合成文本",
    "content-desc": "合成描述",
    class: "android.widget.TextView",
    "resource-id": "com.example.chat:id/input",
    bounds: "[10,20][110,220]",
  };
  const client: AppiumClient = {
    async findElements(using, value) {
      calls.push(["findElements", using, value]);
      return [{ [ELEMENT]: "e1" }, { ELEMENT: "e2" }];
    },
    async getElementAttribute(elementId, name) {
      calls.push(["getElementAttribute", elementId, name]);
      return attributes[name] ?? null;
    },
    async elementClick(elementId) {
      calls.push(["elementClick", elementId]);
    },
    async elementClear(elementId) {
      calls.push(["elementClear", elementId]);
    },
    async elementSendKeys(elementId, text) {
      calls.push(["elementSendKeys", elementId, text]);
    },
    async getPageSource() {
      calls.push(["getPageSource"]);
      return XML;
    },
    async takeScreenshot() {
      calls.push(["takeScreenshot"]);
      return Buffer.from("synthetic png").toString("base64");
    },
    async getWindowRect() {
      calls.push(["getWindowRect"]);
      return { width: 1080, height: 2400 };
    },
    async executeScript(script, args) {
      calls.push(["executeScript", script, ...args]);
      if (script === "mobile: getCurrentPackage") return "com.example.chat";
      if (script === "mobile: getClipboard") return Buffer.from("合成剪贴板").toString("base64");
      return null;
    },
    async performActions(actions) {
      calls.push(["performActions", ...actions]);
    },
    async releaseActions() {
      calls.push(["releaseActions"]);
    },
    async deleteSession() {
      calls.push(["deleteSession"]);
    },
    ...overrides,
  };
  return { client, calls, driver: new AppiumDriver(client, async () => undefined) };
}
const element = { ref: "e1", text: "", desc: "", className: "", resourceId: "", bounds: null };

describe("UiSelector translation", () => {
  it("joins every condition and ignores pick", () => {
    expect(toUiSelector({ id: "com.example.chat:id/input" })).toBe(
      'new UiSelector().resourceId("com.example.chat:id/input")',
    );
    expect(toUiSelector({ id: "chat_new" })).toBe('new UiSelector().resourceId("chat_new")');
    expect(
      toUiSelector({
        text: "新对话",
        textContains: "对话",
        desc: "打开侧边栏",
        descContains: "侧边栏",
        className: "android.widget.TextView",
        pick: "last",
      }),
    ).toBe(
      'new UiSelector().text("新对话").textContains("对话").description("打开侧边栏").descriptionContains("侧边栏").className("android.widget.TextView")',
    );
  });
  it("escapes quotes and backslashes", () => {
    expect(toUiSelector({ text: 'a"b\\c' })).toBe('new UiSelector().text("a\\"b\\\\c")');
  });
  it("sends a text pattern with its backslashes as they are", () => {
    // The server takes what is between the quotes as the pattern and only unescapes quotes.
    expect(toUiSelector({ textMatches: "使用 \\d+ 个工具.*", pick: "first" })).toBe(
      'new UiSelector().textMatches("使用 \\d+ 个工具.*")',
    );
    expect(toUiSelector({ textMatches: '合成"引号"\\.' })).toBe(
      'new UiSelector().textMatches("合成\\"引号\\"\\.")',
    );
    expect(toUiSelector({ textMatches: "搜索网页.*", className: "android.widget.TextView" })).toBe(
      'new UiSelector().textMatches("搜索网页.*").className("android.widget.TextView")',
    );
  });
  it("sends a description pattern and whether the view takes a tap", () => {
    expect(toUiSelector({ descMatches: "[0-9]+个来源" })).toBe(
      'new UiSelector().descriptionMatches("[0-9]+个来源")',
    );
    expect(toUiSelector({ descMatches: "\\d+个来源", clickable: true })).toBe(
      'new UiSelector().descriptionMatches("\\d+个来源").clickable(true)',
    );
    expect(toUiSelector({ desc: "任务", clickable: false })).toBe(
      'new UiSelector().description("任务").clickable(false)',
    );
  });
  it("leaves out what a UiSelector cannot ask for", () => {
    expect(
      toUiSelector({
        className: "android.widget.ImageView",
        unlabeled: true,
        clickable: true,
        row: { size: 3, at: 0 },
        pick: "last",
      }),
    ).toBe('new UiSelector().className("android.widget.ImageView").clickable(true)');
  });
  it("never builds a scroll-until-found expression", () => {
    for (const selector of [{ id: "a" }, { text: "UiScrollable" }, { descContains: "x" }])
      expect(toUiSelector(selector).startsWith("new UiSelector()")).toBe(true);
  });
});

describe("Appium driver", () => {
  it("opens a session that keeps App data, launches nothing and does not wait for idle", () => {
    expect(sessionCapabilities("emulator-5556")).toMatchObject({
      platformName: "Android",
      "appium:automationName": "UiAutomator2",
      "appium:udid": "emulator-5556",
      "appium:noReset": true,
      "appium:autoLaunch": false,
      "appium:settings[waitForIdleTimeout]": 0,
    });
    // Nothing that would install, reset or disguise anything.
    const keys = Object.keys(sessionCapabilities("emulator-5556")).join(" ");
    expect(keys).not.toMatch(/app:|appPackage|appActivity|fullReset|deviceName|locale|language/u);
  });
  it("finds views with one targeted lookup and reads their attributes", async () => {
    const { driver, calls } = fakeClient();
    const found = await driver.find({ id: "com.example.chat:id/input" });
    expect(found).toEqual([
      {
        ref: "e1",
        text: "合成文本",
        desc: "合成描述",
        className: "android.widget.TextView",
        resourceId: "com.example.chat:id/input",
        bounds: { left: 10, top: 20, right: 110, bottom: 220 },
      },
      expect.objectContaining({ ref: "e2" }),
    ]);
    expect(calls[0]).toEqual([
      "findElements",
      "-android uiautomator",
      'new UiSelector().resourceId("com.example.chat:id/input")',
    ]);
    // No hierarchy export for a lookup.
    expect(calls.some((call) => call[0] === "getPageSource")).toBe(false);
  });
  it("filters by label and row what the lookup returned, and taps the element it kept", async () => {
    // What a screen drawn by the App itself gives for its image views: a menu icon, the model
    // name, and a row of three icons below the answer.
    // A view without a resource ID: the server answers the word "null" for it.
    const bare = { text: "", "content-desc": "", "resource-id": "null" };
    const views: Record<string, Record<string, string | null>> = {
      menu: { ...bare, bounds: "[40,150][140,250]" },
      model: { ...bare, "content-desc": "合成模型", bounds: "[190,170][610,240]" },
      copy: { ...bare, bounds: "[42,1353][89,1412]" },
      speak: { ...bare, bounds: "[131,1353][184,1412]" },
      share: { ...bare, bounds: "[226,1353][273,1412]" },
    };
    const { driver, calls } = fakeClient({
      async findElements(using, value) {
        calls.push(["findElements", using, value]);
        return Object.keys(views).map((name) => ({ [ELEMENT]: name }));
      },
      async getElementAttribute(elementId, name) {
        return name === "class" ? "android.widget.ImageView" : (views[elementId]![name] ?? null);
      },
    });
    const icon = { className: "android.widget.ImageView", unlabeled: true as const };
    expect((await driver.find({ className: icon.className }))[0]!.resourceId).toBe("");
    expect((await driver.find(icon)).map((found) => found.ref)).toEqual([
      "menu",
      "copy",
      "speak",
      "share",
    ]);
    const first = await driver.find({ ...icon, clickable: true, row: { size: 3, at: 0 } });
    expect(first.map((found) => found.ref)).toEqual(["copy"]);
    expect((await driver.find({ ...icon, row: { size: 3, at: 2 } }))[0]!.ref).toBe("share");
    expect(await driver.find({ ...icon, row: { size: 2, at: 0 } })).toEqual([]);
    // One lookup each, by what the device can check; nothing is looked up by position.
    expect(calls.filter((call) => call[0] === "findElements").map((call) => call[2])).toEqual([
      'new UiSelector().className("android.widget.ImageView")',
      'new UiSelector().className("android.widget.ImageView")',
      'new UiSelector().className("android.widget.ImageView").clickable(true)',
      'new UiSelector().className("android.widget.ImageView")',
      'new UiSelector().className("android.widget.ImageView")',
    ]);
    await expect(
      driver.tap(first[0]!, async () => {
        throw new Error("lease_expired");
      }),
    ).rejects.toThrow("lease_expired");
    expect(calls.some((call) => call[0] === "elementClick")).toBe(false);
    await driver.tap(first[0]!);
    expect(calls.at(-1)).toEqual(["elementClick", "copy"]);
    expect(calls.some((call) => call[0] === "performActions")).toBe(false);
  });
  it("does not tap a view found by its place once another view answers to its reference", async () => {
    // To the server a view found by a UiSelector is "the n-th view meeting it", looked up again
    // for every command. The reference of the third image view names another view as soon as
    // one before it has gone or a new one has come.
    const bare = { text: "", "content-desc": "", "resource-id": "null" };
    const screen: Record<string, string>[] = [
      { ...bare, bounds: "[40,150][140,250]" },
      { ...bare, bounds: "[42,1353][89,1412]" },
      { ...bare, bounds: "[131,1353][184,1412]" },
      { ...bare, bounds: "[226,1353][273,1412]" },
    ];
    const { driver, calls } = fakeClient({
      async findElements() {
        return screen.map((_, index) => ({ [ELEMENT]: String(index) }));
      },
      async getElementAttribute(elementId, name) {
        const view = screen[Number(elementId)];
        if (!view) throw new Error("no such element: gone");
        return name === "class" ? "android.widget.ImageView" : (view[name] ?? null);
      },
    });
    const copy = { className: "android.widget.ImageView", unlabeled: true as const };
    const row = { ...copy, row: { size: 3, at: 0 } };
    const clicks = () => calls.filter((call) => call[0] === "elementClick");

    // Nothing changed: the tap is the element click it always was.
    const [steady] = await driver.find(row);
    await driver.tap(steady!);
    expect(clicks()).toEqual([["elementClick", "1"]]);

    // The view before it went away: its reference now names the icon to its right.
    const [moved] = await driver.find(row);
    screen.splice(0, 1);
    let reachedClick = false;
    await expect(
      driver.tap(moved!, async () => {
        reachedClick = true;
      }),
    ).rejects.toMatchObject({
      kind: "stale_element",
      command: "elementClick",
    });
    expect(reachedClick).toBe(false);
    // A view with a label took its place.
    screen.splice(0, 0, { ...bare, bounds: "[40,150][140,250]" });
    const [relabelled] = await driver.find(copy);
    screen[0] = { ...screen[0]!, "content-desc": "合成描述" };
    await expect(driver.tap(relabelled!)).rejects.toMatchObject({ kind: "stale_element" });
    // No view answers to the reference any more.
    const found = await driver.find(copy);
    screen.splice(1);
    await expect(driver.tap(found.at(-1)!)).rejects.toMatchObject({ kind: "stale_element" });
    expect(clicks()).toHaveLength(1);

    // A view found by what it says is not looked at again: the device checked its conditions.
    const [labelled] = await driver.find({ className: "android.widget.ImageView" });
    screen[0] = { ...screen[0]!, bounds: "[0,0][10,10]" };
    await driver.tap(labelled!);
    expect(clicks()).toHaveLength(2);
  });
  it("skips a view that went away between the lookup and the read", async () => {
    const { driver } = fakeClient({
      async getElementAttribute(elementId) {
        if (elementId === "e1") throw new Error("stale element reference: gone");
        return "x";
      },
    });
    expect((await driver.find({ id: "a" })).map((item) => item.ref)).toEqual(["e2"]);
  });
  it("taps, sets text as one value and reads it back", async () => {
    const { driver, calls } = fakeClient();
    await driver.tap(element);
    await driver.setText(element, "合成前缀\n\n合成问题");
    expect(await driver.readText(element)).toBe("合成文本");
    expect(calls).toEqual([
      ["elementClick", "e1"],
      ["elementClear", "e1"],
      ["elementSendKeys", "e1", "合成前缀\n\n合成问题"],
      ["getElementAttribute", "e1", "text"],
    ]);
  });
  it("uses mobile commands by package name, never an Activity name", async () => {
    const { driver, calls } = fakeClient();
    await driver.activateApp("com.example.chat");
    await driver.terminateApp("com.example.chat");
    expect(await driver.foregroundPackage()).toBe("com.example.chat");
    await driver.back();
    await driver.home();
    expect(await driver.readClipboard()).toBe("合成剪贴板");
    expect(calls).toEqual([
      ["executeScript", "mobile: activateApp", { appId: "com.example.chat" }],
      ["executeScript", "mobile: terminateApp", { appId: "com.example.chat" }],
      ["executeScript", "mobile: getCurrentPackage", {}],
      ["executeScript", "mobile: pressKey", { keycode: 4 }],
      ["executeScript", "mobile: pressKey", { keycode: 3 }],
      ["executeScript", "mobile: getClipboard", {}],
    ]);
    expect(JSON.stringify(calls)).not.toMatch(/Activity|startActivity/u);
  });
  it("scrolls with a bounded number of plain swipe gestures inside the given area", async () => {
    const { driver, calls } = fakeClient();
    await driver.swipe("down", 2, { left: 0, top: 400, right: 1080, bottom: 2000 });
    const drags = calls.filter((call) => call[0] === "performActions");
    expect(drags).toHaveLength(2);
    expect(calls.filter((call) => call[0] === "releaseActions")).toHaveLength(2);
    // One finger, a straight drag inside the area with the same fixed timings every time.
    // "down": the finger moves down the screen (earlier content).
    expect(drags[0]![1]).toEqual({
      type: "pointer",
      id: "finger",
      parameters: { pointerType: "touch" },
      actions: [
        { type: "pointerMove", duration: 0, x: 540, y: 864 },
        { type: "pointerDown", button: 0 },
        { type: "pause", duration: 100 },
        { type: "pointerMove", duration: 600, x: 540, y: 1536 },
        { type: "pointerUp", button: 0 },
      ],
    });
    expect(drags[1]).toEqual(drags[0]);
    await driver.swipe("up", 1);
    const up = calls.filter((call) => call[0] === "performActions").at(-1)![1] as {
      actions: { y?: number }[];
    };
    expect(up.actions[0]).toMatchObject({ x: 540, y: 1704 });
    expect(up.actions[3]).toMatchObject({ x: 540, y: 696 });
    for (const count of [0, MAX_SWIPES_PER_CALL + 1, 1.5])
      await expect(driver.swipe("up", count)).rejects.toThrow(RangeError);
    // Drags only: no UiScrollable, scrollIntoView, scrollGesture or the driver's fling gesture.
    expect(JSON.stringify(calls)).not.toMatch(
      /UiScrollable|scrollIntoView|scrollGesture|swipeGesture/u,
    );
  });
  it("lifts the finger when a drag fails part-way", async () => {
    const { driver, calls } = fakeClient({
      async performActions() {
        throw Object.assign(new Error("合成界面文字"), { name: "unknown error" });
      },
    });
    const error = await driver.swipe("up", 3).catch((caught) => caught);
    expect(error).toMatchObject({
      name: "DriverError",
      kind: "command_failed",
      command: "performActions",
      errorName: "unknown error",
    });
    // The pointer state is released once and no further drag is tried.
    expect(calls.filter((call) => call[0] === "releaseActions")).toHaveLength(1);

    // A release that fails as well does not hide the drag's own error.
    const dead = fakeClient({
      async performActions() {
        throw new Error("gone");
      },
      async releaseActions() {
        throw new Error("gone too");
      },
    });
    await expect(dead.driver.swipe("up", 1)).rejects.toMatchObject({ command: "performActions" });
  });
  it("retries a failing hierarchy read a bounded number of times", async () => {
    let reads = 0;
    const flaky = fakeClient({
      async getPageSource() {
        reads += 1;
        if (reads < 3) throw new Error("timeout waiting for root");
        return XML;
      },
    });
    expect(await flaky.driver.hierarchy()).toBe(XML);
    expect(reads).toBe(3);

    reads = 0;
    const dead = fakeClient({
      async getPageSource() {
        reads += 1;
        throw new Error("timeout: 合成界面文字");
      },
    });
    const error = await dead.driver.hierarchy().catch((caught) => caught);
    expect(reads).toBe(3);
    // Only the kind leaves the driver, not the raw message.
    expect(error).toMatchObject({ name: "DriverError", kind: "command_failed" });
    expect(String(error)).not.toContain("合成界面文字");
  });
  it("returns an export without views as it is", async () => {
    const empty = '<hierarchy rotation="0"></hierarchy>';
    const { driver } = fakeClient({ getPageSource: async () => empty });
    expect(await driver.hierarchy()).toBe(empty);
  });
  it("writes a private screenshot and maps command errors to their kind", async () => {
    const directory = await mkdtemp(join(tmpdir(), "aivc-appium-driver-"));
    created.push(directory);
    const { driver } = fakeClient();
    const path = join(directory, "final.png");
    await driver.screenshot(path);
    expect(await readFile(path, "utf8")).toBe("synthetic png");
    expect((await lstat(path)).mode & 0o777).toBe(0o600);

    const failing = fakeClient({
      async elementClick() {
        throw new Error("no such element: 合成");
      },
      async executeScript() {
        throw new Error("socket hang up");
      },
    });
    await expect(failing.driver.tap(element)).rejects.toMatchObject({ kind: "stale_element" });
    await expect(failing.driver.home()).rejects.toMatchObject({ kind: "command_failed" });
  });
  it("names the failed command and a protocol error code, and nothing a server or App wrote", async () => {
    const named = (name: string, message: string) => Object.assign(new Error(message), { name });
    const failing = fakeClient({
      async elementClear() {
        throw named("invalid element state", "cannot clear 合成界面文字");
      },
      // Shaped like an error code, but not one: it could be text an App shows.
      async elementClick() {
        throw named("please verify your phone number", "合成界面文字");
      },
      async getPageSource() {
        throw named("WebDriverRequestError", "socket hang up 合成界面文字");
      },
      async executeScript() {
        throw named("unknown error", "Activity com.example.chat/.Main 合成界面文字");
      },
    });
    const caught = async (work: Promise<unknown>) => {
      const error = await work.catch((raised: unknown) => raised);
      expect(error).toMatchObject({ name: "DriverError", message: "command_failed" });
      expect(JSON.stringify(error)).not.toContain("合成界面文字");
      const { command, errorName } = error as { command?: string; errorName?: string };
      return { command, errorName };
    };
    expect(await caught(failing.driver.setText(element, "x"))).toEqual({
      command: "elementClear",
      errorName: "invalid element state",
    });
    expect(await caught(failing.driver.tap(element))).toEqual({
      command: "elementClick",
      errorName: undefined,
    });
    expect(await caught(failing.driver.hierarchy())).toEqual({
      command: "getPageSource",
      errorName: undefined,
    });
    expect(await caught(failing.driver.activateApp("com.example.chat"))).toEqual({
      command: "activateApp",
      errorName: "unknown error",
    });
  });
  it("ends the session on close and tolerates one that is already gone", async () => {
    const { driver, calls } = fakeClient();
    await driver.close();
    expect(calls).toEqual([["deleteSession"]]);
    await fakeClient({
      async deleteSession() {
        throw new Error("invalid session id");
      },
    }).driver.close();
  });
});

describe("Appium server lifecycle", () => {
  function fakeRunner(options: { exits?: boolean } = {}) {
    const started: { file: string; args: string[]; env?: Readonly<Record<string, string>> }[] = [];
    const kills: (NodeJS.Signals | undefined)[] = [];
    let exit: (value: { code: number | null }) => void = () => undefined;
    const runner: CommandRunner = {
      async run() {
        return { code: 1, stdout: "", stderr: "" };
      },
      start(file, args, startOptions) {
        started.push({ file, args: [...args], env: startOptions?.env });
        const exited = new Promise<{ code: number | null }>((resolve) => {
          exit = resolve;
        });
        if (options.exits) exit({ code: 1 });
        return {
          exited,
          kill(signal) {
            kills.push(signal);
            exit({ code: null });
          },
        };
      },
    };
    return { runner, started, kills };
  }
  const env = { ANDROID_HOME: "/sdk", JAVA_HOME: "/jdk", SECRET_TOKEN: "do-not-pass" };

  it("starts its own server on the loopback address and waits for /status", async () => {
    const { runner, started, kills } = fakeRunner();
    const probed: string[] = [];
    const server = await AppiumServer.start({
      runner,
      env,
      findPort: async () => 47_231,
      probe: async (url) => probed.push(url) >= 3,
      sleep: async () => undefined,
    });
    expect(server.port).toBe(47_231);
    expect(started).toEqual([
      {
        file: APPIUM_BIN,
        args: ["server", "--address", "127.0.0.1", "--port", "47231", "--log-level", "error"],
        // Only what the server needs is added to the allowlisted environment.
        env: { ANDROID_HOME: "/sdk", JAVA_HOME: "/jdk", APPIUM_HOME },
      },
    ]);
    expect(APPIUM_BIN.endsWith("/apps/collector/node_modules/.bin/appium")).toBe(true);
    expect(APPIUM_HOME.endsWith("/apps/collector/")).toBe(true);
    expect(probed).toEqual(Array(3).fill("http://127.0.0.1:47231/status"));
    await server.stop();
    expect(kills).toEqual(["SIGTERM"]);

    // Appium's own log goes to a file only when the operator asks for it.
    const logged = fakeRunner();
    await AppiumServer.start({
      runner: logged.runner,
      env,
      port: 47_236,
      logFile: "/private/aivc/appium.log",
      probe: async () => true,
      sleep: async () => undefined,
    });
    expect(logged.started[0]!.args.slice(-2)).toEqual(["--log", "/private/aivc/appium.log"]);
  });
  it("fails as a setup problem when the server exits, never answers or has no SDK", async () => {
    const dead = fakeRunner({ exits: true });
    await expect(
      AppiumServer.start({
        runner: dead.runner,
        env,
        findPort: async () => 47_232,
        probe: async () => false,
        sleep: async () => undefined,
      }),
    ).rejects.toMatchObject({ code: "appium_unavailable" });

    const silent = fakeRunner();
    await expect(
      AppiumServer.start({
        runner: silent.runner,
        env,
        findPort: async () => 47_233,
        probe: async () => false,
        sleep: async () => undefined,
        startTimeoutMs: 2_000,
      }),
    ).rejects.toMatchObject({ code: "appium_unavailable" });
    // The server that never answered is not left running.
    expect(silent.kills).toEqual(["SIGTERM"]);

    const none = fakeRunner();
    await expect(
      AppiumServer.start({ runner: none.runner, env: { JAVA_HOME: "/jdk" } }),
    ).rejects.toMatchObject({
      code: "appium_unavailable",
      issues: [{ path: "ANDROID_HOME" }],
    });
    expect(none.started).toEqual([]);
  });
  it("kills a server that ignores the stop signal", async () => {
    const kills: (NodeJS.Signals | undefined)[] = [];
    let exit: (value: { code: number | null }) => void = () => undefined;
    const runner: CommandRunner = {
      async run() {
        return { code: 1, stdout: "", stderr: "" };
      },
      start() {
        return {
          exited: new Promise((resolve) => {
            exit = resolve;
          }),
          kill(signal) {
            kills.push(signal);
            if (signal === "SIGKILL") exit({ code: null });
          },
        };
      },
    };
    const server = await AppiumServer.start({
      runner,
      env,
      findPort: async () => 47_234,
      probe: async () => true,
      sleep: async () => undefined,
    });
    await server.stop();
    expect(kills).toEqual(["SIGTERM", "SIGKILL"]);
  });
  it("starts one server on first use, one session per open, and stops it on close", async () => {
    const { runner, started, kills } = fakeRunner();
    const connected: [number, string][] = [];
    const drivers = new AppiumDrivers({
      runner,
      env,
      findPort: async () => 47_235,
      probe: async () => true,
      sleep: async () => undefined,
      connect: async (port, serial) => {
        connected.push([port, serial]);
        if (serial === "emulator-9999") throw new Error("device not found");
        return fakeClient().client;
      },
    });
    expect(started).toEqual([]);
    const [first, second] = await Promise.all([
      drivers.open({ deviceId: "AI_Apps_4KB", serial: "emulator-5556" }),
      drivers.open({ deviceId: "AI_Apps_4KB_2", serial: "emulator-5558" }),
    ]);
    expect(first).not.toBe(second);
    expect(started).toHaveLength(1);
    expect(connected).toEqual([
      [47_235, "emulator-5556"],
      [47_235, "emulator-5558"],
    ]);
    // A session that cannot be created is the device's problem, not a setup error.
    await expect(
      drivers.open({ deviceId: "missing", serial: "emulator-9999" }),
    ).rejects.toMatchObject({ name: "DriverError", kind: "unreachable" });
    await drivers.close();
    expect(kills).toEqual(["SIGTERM"]);
    // Nothing to stop when no session was ever opened.
    const idle = fakeRunner();
    await new AppiumDrivers({ runner: idle.runner, env }).close();
    expect(idle.started).toEqual([]);
  });
  it("reports the pinned tool versions and the helper packages for batch.json", async () => {
    expect(await readDriverTools()).toEqual({
      tools: { appium: "3.9.1", "appium-uiautomator2-driver": "8.7.3", webdriverio: "10.2.0" },
      helperPackages: APPIUM_HELPER_PACKAGES,
    });
    expect((await readDriverTools("/nonexistent")).tools.appium).toBe("unknown");
  });
});
