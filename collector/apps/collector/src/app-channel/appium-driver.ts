import { writeFile } from "node:fs/promises";
import { AppiumServer, readDriverTools, type AppiumServerOptions } from "./appium-server.ts";
import {
  DriverError,
  MAX_SWIPES_PER_CALL,
  inRow,
  isUnlabeled,
  type SELECTOR_CONDITIONS,
  type DeviceDriver,
  type DriverProvider,
  type DriverTools,
  type Selector,
  type SwipeDirection,
  type UiElement,
} from "./driver.ts";
import { parseBounds, parseHierarchy, type Bounds } from "./hierarchy.ts";
import { realSleep, type Sleep } from "./time.ts";

// The real driver: a thin translation of `DeviceDriver` to Appium's UiAutomator2 driver through
// WebdriverIO protocol commands. All flow logic lives behind the interface (adapter.ts).
//
// - The session never resets or launches an App (`noReset`, `autoLaunch: false`), so App data and
//   login state are untouched, and it does not wait for the UI to be idle, so a streaming answer
//   can be looked at.
// - Lookups are plain UiSelector queries. Scrolling is the plain swipe gesture only; no
//   scroll-until-found helper of the automation framework is ever used.
// - Nothing about the emulator is hidden and this code changes no device property. The
//   UiAutomator2 driver itself does two things at every session start that it needs in order to
//   work: it relaxes the global hidden-API policy for the session (restored when the session
//   ends) and starts its helper App `io.appium.settings`. And for every clipboard read on
//   Android 10 or later (`mobile: getClipboard`, used by the copy method) it makes its own input
//   method the device's default for the moment of the read and then restores the previous one
//   (appium-adb `runInImeContext`); a run killed inside that moment leaves the helper's input
//   method selected. None of this disguises the device.

// The protocol commands this driver uses; a WebdriverIO session provides them.
export interface AppiumClient {
  findElements(using: string, value: string): Promise<unknown>;
  getElementAttribute(elementId: string, name: string): Promise<string | null>;
  elementClick(elementId: string): Promise<unknown>;
  elementClear(elementId: string): Promise<unknown>;
  elementSendKeys(elementId: string, text: string): Promise<unknown>;
  getPageSource(): Promise<string>;
  takeScreenshot(): Promise<string>;
  getWindowRect(): Promise<{ width: number; height: number }>;
  executeScript(script: string, args: unknown[]): Promise<unknown>;
  performActions(actions: unknown[]): Promise<unknown>;
  releaseActions(): Promise<unknown>;
  deleteSession(): Promise<unknown>;
}

const W3C_ELEMENT = "element-6066-11e4-a52e-4f735466cecf";
const KEYCODE_HOME = 3;
const KEYCODE_BACK = 4;
const HIERARCHY_ATTEMPTS = 3;
const HIERARCHY_RETRY_MS = 1_000;
// Share of the swipe area one gesture covers, and how far the area stays away from the edges of
// the view it scrolls (system gesture zones, sticky headers).
const SWIPE_PERCENT = 0.6;
const SWIPE_INSET = 0.15;
// One swipe is a straight drag of one finger: touch, hold, move, lift, always with these same
// timings. The driver's own `mobile: swipeGesture` is a fling that Compose lists ignore (real
// device, DeepSeek 2.8.0: the conversation did not move), so it is not used.
const SWIPE_HOLD_MS = 100;
const SWIPE_MOVE_MS = 600;
const SWIPE_SETTLE_MS = 400;

// The conditions a UiSelector can express, in the order they are written into the expression.
const UI_SELECTOR_METHODS = {
  id: "resourceId",
  text: "text",
  textContains: "textContains",
  textMatches: "textMatches",
  desc: "description",
  descContains: "descriptionContains",
  descMatches: "descriptionMatches",
  className: "className",
} as const satisfies Partial<Record<(typeof SELECTOR_CONDITIONS)[number], string>>;
const UI_SELECTOR_PATTERNS: ReadonlySet<string> = new Set(["textMatches", "descMatches"]);
// A UiSelector expression with every condition the device can check. `unlabeled` and `row` are
// not among them: a UiSelector cannot ask for a view without a description, nor compare a view
// with its neighbours. `find` applies both to what the lookup returns.
export function toUiSelector(selector: Selector): string {
  const quote = (value: string) => `"${value.replaceAll("\\", "\\\\").replaceAll('"', '\\"')}"`;
  // A regular expression keeps its backslashes as they are: the server's expression parser
  // takes the text between the quotes as the argument and only turns an escaped quote back into
  // a quote (appium-uiautomator2-server, UiExpressionParser), so a doubled backslash would reach
  // UiSelector doubled and a digit class would no longer be one.
  const quotePattern = (value: string) => `"${value.replaceAll('"', '\\"')}"`;
  const expression = (
    Object.keys(UI_SELECTOR_METHODS) as (keyof typeof UI_SELECTOR_METHODS)[]
  ).reduce((built, key) => {
    const value = selector[key];
    return value === undefined
      ? built
      : `${built}.${UI_SELECTOR_METHODS[key]}(${(UI_SELECTOR_PATTERNS.has(key) ? quotePattern : quote)(value)})`;
  }, "new UiSelector()");
  return selector.clickable === undefined
    ? expression
    : `${expression}.clickable(${selector.clickable ? "true" : "false"})`;
}

function elementId(reference: unknown): string | undefined {
  if (typeof reference !== "object" || reference === null) return undefined;
  const id =
    (reference as Record<string, unknown>)[W3C_ELEMENT] ??
    (reference as Record<string, unknown>).ELEMENT;
  return typeof id === "string" ? id : undefined;
}
// The error codes of the W3C WebDriver protocol. A server answers with one of them and the
// client copies it into the error's name; a name that is not on this list is dropped, so the
// log field is a closed vocabulary whatever a server or an App puts into an error.
const W3C_ERROR_CODES: ReadonlySet<string> = new Set([
  "detached shadow root",
  "element click intercepted",
  "element not interactable",
  "insecure certificate",
  "invalid argument",
  "invalid cookie domain",
  "invalid element state",
  "invalid selector",
  "invalid session id",
  "javascript error",
  "move target out of bounds",
  "no such alert",
  "no such cookie",
  "no such element",
  "no such frame",
  "no such shadow root",
  "no such window",
  "script timeout",
  "session not created",
  "stale element reference",
  "timeout",
  "unable to capture screen",
  "unable to set cookie",
  "unexpected alert open",
  "unknown command",
  "unknown error",
  "unknown method",
  "unsupported operation",
]);
function driverError(error: unknown, command?: string): DriverError {
  if (error instanceof DriverError) return error;
  const text = error instanceof Error ? `${error.name} ${error.message}` : "";
  const name = error instanceof Error && W3C_ERROR_CODES.has(error.name) ? error.name : undefined;
  return new DriverError(
    /stale element|no such element/iu.test(text) ? "stale_element" : "command_failed",
    command,
    name,
  );
}

const sameBounds = (a: Bounds | null, b: Bounds | null) =>
  a === null || b === null
    ? a === b
    : a.left === b.left && a.top === b.top && a.right === b.right && a.bottom === b.bottom;

export class AppiumDriver implements DeviceDriver {
  private screen: { width: number; height: number } | undefined;
  // The elements `find` kept by a condition the device did not check (`unlabeled`, `row`). To
  // the server an element found by a UiSelector is not a view but that selector and a number:
  // "the n-th view meeting it", looked up again for every command (UiAutomator's UiObject). For
  // a selector that says what the view shows, whatever answers later still meets it. Here the
  // selector the server has is only the class: once a view before this one has gone or come,
  // the same reference names a neighbour, the icon next to the one that was found. `tap`
  // therefore looks at such an element once more.
  private readonly foundByPlace = new WeakSet<UiElement>();

  constructor(
    private readonly client: AppiumClient,
    private readonly sleep: Sleep = realSleep,
  ) {}

  // Raw driver errors may quote App text; only the kind leaves this class.
  private async call<T>(name: string, command: () => Promise<T>): Promise<T> {
    try {
      return await command();
    } catch (error) {
      throw driverError(error, name);
    }
  }
  private mobile(command: string, args: Record<string, unknown> = {}): Promise<unknown> {
    return this.call(command, () => this.client.executeScript(`mobile: ${command}`, [args]));
  }

  async activateApp(packageName: string): Promise<void> {
    await this.mobile("activateApp", { appId: packageName });
  }
  async terminateApp(packageName: string): Promise<void> {
    await this.mobile("terminateApp", { appId: packageName });
  }
  async foregroundPackage(): Promise<string | null> {
    const name = await this.mobile("getCurrentPackage");
    return typeof name === "string" && name !== "" ? name : null;
  }

  async find(selector: Selector): Promise<UiElement[]> {
    const found = await this.call("findElements", () =>
      this.client.findElements("-android uiautomator", toUiSelector(selector)),
    );
    const elements: UiElement[] = [];
    for (const reference of Array.isArray(found) ? found : []) {
      const ref = elementId(reference);
      if (ref === undefined) continue;
      try {
        elements.push({ ref, ...(await this.look(ref)) });
      } catch (error) {
        // The view went away between the lookup and the read: it is no longer visible.
        if (driverError(error).kind !== "stale_element")
          throw driverError(error, "getElementAttribute");
      }
    }
    // The conditions no UiSelector expresses, applied to what the lookup returned. The elements
    // stay the driver's own: a tap on one is the same element click as on any other.
    const kept = inRow(selector.unlabeled ? elements.filter(isUnlabeled) : elements, selector);
    if (selector.unlabeled || selector.row)
      for (const element of kept) this.foundByPlace.add(element);
    return kept;
  }
  // What the server says of the view a reference names right now.
  private async look(ref: string): Promise<Omit<UiElement, "ref">> {
    const attribute = async (name: string) =>
      (await this.client.getElementAttribute(ref, name)) ?? "";
    return {
      text: await attribute("text"),
      desc: await attribute("content-desc"),
      className: await attribute("class"),
      // The UiAutomator2 server answers the literal word for a view without a resource ID
      // (real device, 2026-11-16). No resource ID reads like that: an ID has a colon and a
      // slash, or is a test tag an App chose.
      resourceId: (await attribute("resource-id")).replace(/^null$/u, ""),
      bounds: parseBounds(await attribute("bounds")),
    };
  }
  async tap(element: UiElement, beforeClick?: () => Promise<void>): Promise<void> {
    if (this.foundByPlace.has(element)) {
      // Still the view that was found: the same labels (none, for a view without any) at the
      // same place. Anything else is another view answering to the reference, or the screen
      // moving under the step; neither is tapped.
      const now = await this.call("elementClick", () => this.look(element.ref));
      if (
        now.text !== element.text ||
        now.desc !== element.desc ||
        now.className !== element.className ||
        now.resourceId !== element.resourceId ||
        !sameBounds(now.bounds, element.bounds)
      )
        throw new DriverError("stale_element", "elementClick");
    }
    if (beforeClick) await beforeClick();
    await this.call("elementClick", () => this.client.elementClick(element.ref));
  }
  async setText(element: UiElement, text: string): Promise<void> {
    await this.call("elementClear", () => this.client.elementClear(element.ref));
    if (text !== "")
      await this.call("elementSendKeys", () => this.client.elementSendKeys(element.ref, text));
  }
  async readText(element: UiElement): Promise<string> {
    return (
      (await this.call("getElementAttribute", () =>
        this.client.getElementAttribute(element.ref, "text"),
      )) ?? ""
    );
  }

  async swipe(direction: SwipeDirection, count: number, within?: Bounds): Promise<void> {
    if (!Number.isInteger(count) || count < 1 || count > MAX_SWIPES_PER_CALL)
      throw new RangeError("swipe count out of bounds");
    this.screen ??= await this.call("getWindowRect", () => this.client.getWindowRect());
    const { width, height } = this.screen;
    const box = within ?? { left: 0, top: 0, right: width, bottom: height };
    const boxWidth = box.right - box.left;
    const boxHeight = box.bottom - box.top;
    const area = {
      left: Math.round(box.left + boxWidth * SWIPE_INSET),
      top: Math.round(box.top + boxHeight * SWIPE_INSET),
      width: Math.round(boxWidth * (1 - 2 * SWIPE_INSET)),
      height: Math.round(boxHeight * (1 - 2 * SWIPE_INSET)),
    };
    if (area.width <= 0 || area.height <= 0) throw new DriverError("command_failed");
    const x = Math.round(area.left + area.width / 2);
    const low = Math.round(area.top + area.height * (0.5 + SWIPE_PERCENT / 2));
    const high = Math.round(area.top + area.height * (0.5 - SWIPE_PERCENT / 2));
    // "up": the finger moves up, so later content comes on screen.
    const [from, to] = direction === "up" ? [low, high] : [high, low];
    for (let index = 0; index < count; index += 1) {
      try {
        await this.call("performActions", () =>
          this.client.performActions([
            {
              type: "pointer",
              id: "finger",
              parameters: { pointerType: "touch" },
              actions: [
                { type: "pointerMove", duration: 0, x, y: from },
                { type: "pointerDown", button: 0 },
                { type: "pause", duration: SWIPE_HOLD_MS },
                { type: "pointerMove", duration: SWIPE_MOVE_MS, x, y: to },
                { type: "pointerUp", button: 0 },
              ],
            },
          ]),
        );
      } catch (error) {
        // A drag that failed part-way may have left the finger down: lift it before the failure
        // is reported, so the screen is not held while the evidence is taken.
        await this.client.releaseActions().catch(() => undefined);
        throw error;
      }
      await this.call("releaseActions", () => this.client.releaseActions());
      await this.sleep(SWIPE_SETTLE_MS);
    }
  }
  async back(): Promise<void> {
    await this.mobile("pressKey", { keycode: KEYCODE_BACK });
  }
  async home(): Promise<void> {
    await this.mobile("pressKey", { keycode: KEYCODE_HOME });
  }

  // A read that fails or times out is repeated a bounded number of times; an export without any
  // view is returned as it is and the caller decides whether to wait for the App.
  async hierarchy(): Promise<string> {
    let failure: unknown;
    for (let attempt = 1; attempt <= HIERARCHY_ATTEMPTS; attempt += 1) {
      try {
        const xml = await this.client.getPageSource();
        parseHierarchy(xml);
        return xml;
      } catch (error) {
        failure = error;
      }
      if (attempt < HIERARCHY_ATTEMPTS) await this.sleep(HIERARCHY_RETRY_MS);
    }
    throw driverError(failure, "getPageSource");
  }
  async screenshot(path: string): Promise<void> {
    const png = await this.call("takeScreenshot", () => this.client.takeScreenshot());
    await writeFile(path, Buffer.from(png, "base64"), { mode: 0o600, flag: "wx" });
  }
  async readClipboard(): Promise<string> {
    const content = await this.mobile("getClipboard");
    return typeof content === "string" ? Buffer.from(content, "base64").toString("utf8") : "";
  }
  async close(): Promise<void> {
    await this.client.deleteSession().catch(() => undefined);
  }
}

// Session settings (stage 0): keep App data and login state, start nothing, never wait for an
// idle UI. Only the declared emulator is addressed, by its serial.
export function sessionCapabilities(serial: string): Record<string, unknown> {
  return {
    platformName: "Android",
    "appium:automationName": "UiAutomator2",
    "appium:udid": serial,
    "appium:noReset": true,
    "appium:autoLaunch": false,
    "appium:settings[waitForIdleTimeout]": 0,
    // An attempt may wait minutes for an answer between two commands.
    "appium:newCommandTimeout": 900,
    // Plain WebDriver commands only.
    "wdio:enforceWebDriverClassic": true,
  };
}
async function connectWebdriverIO(port: number, serial: string): Promise<AppiumClient> {
  // Loaded on first use: `check` without a contract directory and the tests never need it.
  const { remote } = await import("webdriverio");
  const session = await remote({
    hostname: "127.0.0.1",
    port,
    path: "/",
    // Its own log lines would carry element text.
    logLevel: "silent",
    connectionRetryCount: 0,
    connectionRetryTimeout: 120_000,
    capabilities: sessionCapabilities(serial),
  });
  return session as unknown as AppiumClient;
}

export interface AppiumDriversOptions extends AppiumServerOptions {
  connect?: (port: number, serial: string) => Promise<AppiumClient>;
  startServer?: (options: AppiumServerOptions) => Promise<Pick<AppiumServer, "port" | "stop">>;
}
// One Appium server for the script, started on first use; one session per attempt on a device.
export class AppiumDrivers implements DriverProvider {
  private server: Promise<Pick<AppiumServer, "port" | "stop">> | undefined;

  constructor(private readonly options: AppiumDriversOptions) {}

  async open(target: { deviceId: string; serial: string }): Promise<DeviceDriver> {
    // A server that cannot start is a setup problem and ends the run; a session that cannot be
    // created is the device's problem.
    const server = await (this.server ??= (this.options.startServer ?? AppiumServer.start)(
      this.options,
    ));
    try {
      const client = await (this.options.connect ?? connectWebdriverIO)(server.port, target.serial);
      return new AppiumDriver(client, this.options.sleep);
    } catch {
      throw new DriverError("unreachable");
    }
  }
  describe(): Promise<DriverTools> {
    return readDriverTools();
  }
  async close(): Promise<void> {
    const server = await this.server?.catch(() => undefined);
    this.server = undefined;
    await server?.stop();
  }
}
