import { writeFile } from "node:fs/promises";
import {
  DriverError,
  MAX_SWIPES_PER_CALL,
  inRow,
  isUnlabeled,
  type DeviceDriver,
  type DriverProvider,
  type Selector,
  type SwipeDirection,
  type UiElement,
} from "./driver.ts";
import type { AppFailureCode } from "./failure.ts";
import { matchesSelector, type Bounds } from "./hierarchy.ts";

// Scripted device for tests: a model renders the views of its current screen and reacts to taps,
// text and swipes; the driver exposes them through the same interface and the same hierarchy
// XML format as the real one. Synthetic screens only.

export interface MockNode {
  id?: string;
  text?: string;
  desc?: string;
  className?: string;
  children?: MockNode[];
  // Screen rows a list item takes (default 1). An item taller than the list's viewport stays
  // the only view on screen while it scrolls.
  rows?: number;
  // Height of the view's own bounds in pixels (default: one row), for views whose height is
  // looked at.
  height?: number;
  // The view sits to the right of the view before it, in the same row of the screen: same top
  // and bottom edge (a row of icons).
  beside?: boolean;
  onTap?: () => void;
  onText?: (text: string) => void;
  // What the view says about taking a tap, where that is not what it does (default: it says it
  // takes one when it does). An App that draws its own screen can leave a control that works
  // unmarked.
  clickable?: boolean;
}
export interface MockScreen {
  // Package of the focused window.
  foreground: string | null;
  nodes: MockNode[];
}
export interface MockModel {
  screen(): MockScreen;
  activate(packageName: string): void;
  terminate(packageName: string): void;
  back(): void;
  home(): void;
  swipe(direction: SwipeDirection, within?: Bounds): void;
  clipboard(): string;
}

type DriverMethod = keyof DeviceDriver;
export interface MockDriverOptions {
  // Called before every command; throw to make that command fail.
  intercept?: (method: DriverMethod) => void;
  // The first hierarchy exports come back without a single view (cold start).
  emptyReads?: number;
}

interface Rendered extends UiElement {
  node: MockNode;
  depth: number;
  clickable: boolean;
}
// Width of a view that shares its row with others.
const ICON_WIDTH = 120;
const ROW_HEIGHT = 100;
const DEFAULT_CLASS = "android.view.View";
const escapeXml = (value: string) =>
  value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("\n", "&#10;");

export class MockDriver implements DeviceDriver {
  readonly calls: DriverMethod[] = [];
  readonly swipes: { direction: SwipeDirection; count: number; within?: Bounds }[] = [];
  // Every selector a lookup was made with.
  readonly lookups: Selector[] = [];
  closed = false;
  private emptyReads: number;
  private counter = 0;

  constructor(
    private readonly model: MockModel,
    private readonly options: MockDriverOptions = {},
  ) {
    this.emptyReads = options.emptyReads ?? 0;
  }

  private enter(method: DriverMethod): void {
    this.calls.push(method);
    this.options.intercept?.(method);
  }
  // The visible views in screen order, one row each.
  private render(): { foreground: string | null; views: Rendered[] } {
    const { foreground, nodes } = this.model.screen();
    const views: Rendered[] = [];
    const visit = (node: MockNode, depth: number) => {
      const before = node.beside ? views.at(-1)?.bounds : undefined;
      const top = before ? before.top : views.length * ROW_HEIGHT;
      const left = before ? before.left + ICON_WIDTH : 0;
      views.push({
        node,
        depth,
        ref: "",
        text: node.text ?? "",
        desc: node.desc ?? "",
        className: node.className ?? DEFAULT_CLASS,
        resourceId: node.id ?? "",
        clickable: node.clickable ?? node.onTap !== undefined,
        bounds: before
          ? { left, top, right: left + ICON_WIDTH, bottom: before.bottom }
          : { left, top, right: 1080, bottom: top + (node.height ?? ROW_HEIGHT) },
      });
      for (const child of node.children ?? []) visit(child, depth + 1);
    };
    for (const node of nodes) visit(node, 0);
    return { foreground, views };
  }
  // The view an earlier lookup returned, as it is on the screen now. Screens are re-rendered on
  // every look, so identity is the identifier (or, without one, the text and description).
  private current(element: UiElement): Rendered {
    const same = (view: Rendered) =>
      view.className === element.className &&
      (element.resourceId !== ""
        ? view.resourceId === element.resourceId
        : view.resourceId === "" && view.text === element.text && view.desc === element.desc);
    // Of several views that look alike, the one at the place the lookup found it. A view without
    // any label is known by its place only: with none there it is gone, as the real driver has
    // it, and the command does not fall to another view that looks the same.
    const views = this.render().views.filter(same);
    const view =
      views.find((item) => JSON.stringify(item.bounds) === JSON.stringify(element.bounds)) ??
      (isUnlabeled(element) ? undefined : views[0]);
    if (!view) throw new DriverError("stale_element");
    return view;
  }

  async activateApp(packageName: string): Promise<void> {
    this.enter("activateApp");
    this.model.activate(packageName);
  }
  async terminateApp(packageName: string): Promise<void> {
    this.enter("terminateApp");
    this.model.terminate(packageName);
  }
  async foregroundPackage(): Promise<string | null> {
    this.enter("foregroundPackage");
    return this.model.screen().foreground;
  }
  async find(selector: Selector): Promise<UiElement[]> {
    this.enter("find");
    this.lookups.push(selector);
    return inRow(
      this.render().views.filter((view) => matchesSelector(view, selector)),
      selector,
    ).map(({ text, desc, className, resourceId, bounds }) => ({
      ref: `mock-${(this.counter += 1)}`,
      text,
      desc,
      className,
      resourceId,
      bounds,
    }));
  }
  async tap(element: UiElement, beforeClick?: () => Promise<void>): Promise<void> {
    if (beforeClick) await beforeClick();
    this.enter("tap");
    this.current(element).node.onTap?.();
  }
  async setText(element: UiElement, text: string): Promise<void> {
    this.enter("setText");
    const { node } = this.current(element);
    if (!node.onText) throw new DriverError("command_failed");
    node.onText(text);
  }
  async readText(element: UiElement): Promise<string> {
    this.enter("readText");
    return this.current(element).text;
  }
  async swipe(direction: SwipeDirection, count: number, within?: Bounds): Promise<void> {
    this.enter("swipe");
    if (!Number.isInteger(count) || count < 1 || count > MAX_SWIPES_PER_CALL)
      throw new RangeError("swipe count out of bounds");
    this.swipes.push({ direction, count, ...(within ? { within } : {}) });
    for (let index = 0; index < count; index += 1) this.model.swipe(direction, within);
  }
  async back(): Promise<void> {
    this.enter("back");
    this.model.back();
  }
  async home(): Promise<void> {
    this.enter("home");
    this.model.home();
  }
  async hierarchy(): Promise<string> {
    this.enter("hierarchy");
    const header = "<?xml version='1.0' encoding='UTF-8' standalone='yes' ?>";
    const open = '<hierarchy index="0" class="hierarchy" rotation="0" width="1080" height="2400">';
    if (this.emptyReads > 0) {
      this.emptyReads -= 1;
      return `${header}\n${open}\n</hierarchy>`;
    }
    const { foreground, views } = this.render();
    const lines: string[] = [];
    const stack: Rendered[] = [];
    const closeTo = (depth: number) => {
      while (stack.length > depth) {
        const view = stack.pop()!;
        lines.push(`${"  ".repeat(view.depth + 1)}</${view.className}>`);
      }
    };
    views.forEach((view, index) => {
      closeTo(view.depth);
      const box = view.bounds!;
      const attributes = [
        `index="${index}"`,
        `package="${escapeXml(foreground ?? "")}"`,
        `class="${escapeXml(view.className)}"`,
        `text="${escapeXml(view.text)}"`,
        `content-desc="${escapeXml(view.desc)}"`,
        `resource-id="${escapeXml(view.resourceId)}"`,
        `clickable="${view.clickable ? "true" : "false"}"`,
        `bounds="[${box.left},${box.top}][${box.right},${box.bottom}]"`,
        'displayed="true"',
      ].join(" ");
      const hasChildren = views[index + 1] !== undefined && views[index + 1]!.depth > view.depth;
      lines.push(
        `${"  ".repeat(view.depth + 1)}<${view.className} ${attributes}${hasChildren ? "" : " /"}>`,
      );
      if (hasChildren) stack.push(view);
    });
    closeTo(0);
    return [header, open, ...lines, "</hierarchy>"].join("\n");
  }
  async screenshot(path: string): Promise<void> {
    this.enter("screenshot");
    await writeFile(path, "synthetic png bytes", { mode: 0o600, flag: "wx" });
  }
  async readClipboard(): Promise<string> {
    this.enter("readClipboard");
    return this.model.clipboard();
  }
  async close(): Promise<void> {
    this.enter("close");
    this.closed = true;
  }
}

// One mock driver per session over a shared model per device.
export class MockDrivers implements DriverProvider {
  readonly opened: MockDriver[] = [];
  closed = false;
  constructor(
    private readonly models: Record<string, MockModel>,
    private readonly options: (deviceId: string) => MockDriverOptions = () => ({}),
  ) {}
  async open(target: { deviceId: string; serial: string }): Promise<DeviceDriver> {
    const model = this.models[target.deviceId];
    if (!model) throw new DriverError("unreachable");
    const driver = new MockDriver(model, this.options(target.deviceId));
    this.opened.push(driver);
    return driver;
  }
  async describe() {
    return { tools: { "mock-driver": "1" }, helperPackages: [] };
  }
  async close(): Promise<void> {
    this.closed = true;
  }
}

export interface MockSource {
  title: string;
  site?: string;
  url?: string;
  // Own height of this source's card in a list of equally tall cards, where it is not the usual
  // one: the first and the last card of a real list carry the list's padding.
  cardHeight?: number;
}
type Blocker = Extract<
  AppFailureCode,
  "login_required" | "challenge_required" | "rate_limited" | "emulator_rejected"
>;
export interface MockChatOptions {
  now: () => Date;
  loggedIn?: boolean;
  // A one-time guide panel covers the App until it is dismissed.
  popup?: boolean;
  // A dialog no contract declares.
  undeclaredDialog?: "launch" | "after_send";
  blocker?: { code: Blocker; when: "launch" | "after_send" };
  // "launch": the App never comes to the foreground. "after_send": it leaves it.
  crash?: "launch" | "after_send";
  // Cold start: the launcher stays in front for this many looks at the screen.
  slowStartReads?: number;
  // Cold start: the system "not responding" dialog shows until "wait" was chosen this often.
  startupAnr?: number;
  // The system "isn't responding" dialog appears once the question is sent.
  anr?: boolean;
  generationMs?: number;
  neverCompletes?: boolean;
  // Answer paragraphs, one text view each.
  answer?: string[];
  // The App shows the sent question back as its answer.
  echo?: boolean;
  // Rows of the message list that fit on the screen.
  viewport?: number;
  // The composer keeps something other than what was set.
  composerDropsBlankLines?: boolean;
  // The bubble shows the sent text with different spacing: a space between Chinese and Latin
  // text and after a Chinese comma, as Apps add them.
  respaceBubble?: boolean;
  // A tap on a source entry (that of the source panel and that of the tool steps) opens nothing.
  sourceEntryDead?: boolean;
  // A tap on the entry of the source panel opens nothing; the entry of the tool steps works.
  panelEntryDead?: boolean;
  // The entry of the source panel sits below the answer text instead of above it. An answer
  // written with tools (`toolSteps`) then has two source entries: that of the tool steps at
  // its start and this one at its end.
  sourceEntryAtEnd?: boolean;
  citations?: MockSource[];
  // The search-keyword line of the source panel.
  queries?: string;
  sourcesViewport?: number;
  // Every source is a clickable card without resource IDs (an ID-less App's source list).
  sourceCards?: boolean;
  // Suggested follow-ups below the answer controls, as plain text views without any ID.
  followUps?: string[];
  // The whole answer is one text view this many rows tall (an App that does not split its
  // answer): scrolling inside it changes nothing in the hierarchy.
  tallAnswerRows?: number;
  // The answer controls are already there while the answer is still being written, and the
  // answer text grows from look to look.
  controlsWhileStreaming?: boolean;
  // The source panel closes only on this many back presses (a sheet dragged to full height).
  sourcePanelBacks?: number;
  // The stated number sits in a row of the source list and scrolls away with it, instead of in
  // the panel's fixed title.
  statedSourcesInList?: boolean;
  // The source panel's title states this many sources ("参考资料 3").
  statedSources?: number;
  // After the copy control is used, the system's clipboard preview hides the conversation for
  // this long: only the top bar stays visible.
  copyOverlayMs?: number;
  // Answers by conversation, in the order the conversations are sent; the last one repeats.
  answers?: string[][];
  // From this conversation on (1 = the first one sent) the answer's copy control does nothing:
  // the clipboard keeps what an earlier copy put there.
  copyDeadFrom?: number;
  // A second copy control inside the answer (a code block's), found by the same selector as the
  // answer's own. It copies only its block.
  inlineCopy?: boolean;
  // With `sourceCards`: the card just above the visible part of the list shows without its first
  // line, as a card cut by the top edge of the list does.
  cutTopCard?: boolean;
  // The source panel stays on screen this long after the back press that closes it.
  panelCloseMs?: number;
  // The source panel is a sheet in the conversation's own window: the conversation's views stay
  // in the hierarchy under it, but a tap on one of them lands on the sheet and does nothing.
  // The tool-step screen of `toolSteps` is then such a sheet as well.
  sourcePanelOverConversation?: boolean;
  // The source list is one of equally tall cards without resource IDs that hold a title and,
  // where given, a site. With `cutTopCard` the card just above the visible part of the list
  // shows as a lower card with its title only.
  equalCards?: boolean;
  // A swipe moves the source list by a whole screen: two reads share no whole card.
  sourcesPageSwipe?: boolean;
  // The answer was written with tools. It then starts with a clickable line that opens the
  // tool-step screen with one clickable row per step. A step with `sources` leads to a source
  // list of its own (equally tall cards, as with `equalCards`), from which the back key returns
  // to the steps; a second press returns to the start of the conversation. `sourceEntryDead`,
  // `sourcePanelBacks`, `sourcesViewport` and `cutTopCard` apply to these screens as well.
  toolSteps?: { text: string; sources?: MockSource[] }[];
  // A tap on a step that has sources opens nothing.
  toolRowDead?: boolean;
  // The tool-step screen closes only on this many back presses.
  toolScreenBacks?: number;
  // The back key on a step's source list leaves the tool-step screen as well.
  toolListExitsToChat?: boolean;
  missing?: "new_conversation" | "send" | "sources_close";
}

export const MOCK_PACKAGE = "com.larus.nova";
const LAUNCHER = "com.android.launcher3";
const id = (name: string) => `${MOCK_PACKAGE}:id/${name}`;
const TEXT_VIEW = "android.widget.TextView";
// Own height of an equally tall source card, and of what an edge of the list leaves of one.
const CARD_HEIGHT = 160;
const CUT_CARD_HEIGHT = 60;

// A chat App shaped like the Doubao reference case of the contract fixture: sidebar with a
// "new conversation" entry and the account name, voice/text composer, a message list that does
// not follow the answer, a jump-to-end button, action buttons that appear when generation ends,
// and a source panel.
export class MockChatApp implements MockModel {
  // Every text that was actually sent.
  readonly sent: string[] = [];
  running = false;
  private foregrounded = false;
  private view: "chat" | "sidebar" | "sources" | "tools" = "chat";
  // The list a tool step opened, while it is open.
  private toolSources: MockSource[] | null = null;
  private toolBacks = 0;
  private mode: "voice" | "text" = "voice";
  private input = "";
  private conversation: { text: string; at: number } | null = null;
  private top = 0;
  private sourcesTop = 0;
  private popupOpen: boolean;
  private copied = "";
  private startingReads = 0;
  private startupDialogs = 0;
  private copiedAt: number | null = null;
  private panelBacks = 0;
  private panelClosingAt: number | null = null;

  constructor(private readonly options: MockChatOptions) {
    this.popupOpen = options.popup ?? false;
  }

  private get complete(): boolean {
    return (
      this.conversation !== null &&
      !this.options.neverCompletes &&
      this.options.now().getTime() - this.conversation.at >= (this.options.generationMs ?? 20_000)
    );
  }
  private applies(when: "launch" | "after_send" | undefined): boolean {
    return when === "launch" || (when === "after_send" && this.conversation !== null);
  }
  // The sent text as the App shows it.
  private shownQuestion(): string {
    const text = this.conversation?.text ?? "";
    return this.options.respaceBubble
      ? text
          .replaceAll("\n\n", "\n \n")
          .replaceAll("，", "， ")
          .replace(/(\p{Script=Han})([A-Za-z0-9])/gu, "$1 $2")
          .replace(/([A-Za-z0-9])(\p{Script=Han})/gu, "$1 $2")
      : text;
  }
  private answer(): string[] {
    if (this.options.echo) return [this.shownQuestion()];
    const answers = this.options.answers;
    if (answers && answers.length > 0)
      return answers[Math.min(Math.max(this.sent.length, 1), answers.length) - 1]!;
    return this.options.answer ?? ["合成回答第一段", "合成回答第二段"];
  }
  private messages(): MockNode[] {
    if (!this.conversation) return [];
    // The clipboard preview covers the conversation.
    if (
      this.copiedAt !== null &&
      this.options.now().getTime() - this.copiedAt < (this.options.copyOverlayMs ?? 0)
    )
      return [];
    const answer = this.answer();
    const sources = this.options.citations ?? [];
    const items: MockNode[] = [
      { id: id("mock_sent_text"), text: this.shownQuestion(), className: TEXT_VIEW },
    ];
    const openSources = () => {
      if (this.options.sourceEntryDead || this.options.panelEntryDead) return;
      this.view = "sources";
      this.sourcesTop = 0;
    };
    const steps = this.options.toolSteps ?? [];
    if (this.complete && steps.length > 0)
      items.push({
        id: id("mock_tool_summary"),
        className: TEXT_VIEW,
        text: `使用 ${steps.length} 个工具，合成说明`,
        onTap: () => {
          if (!this.options.sourceEntryDead) this.view = "tools";
        },
      });
    if (!this.complete) {
      if (!this.options.controlsWhileStreaming)
        return [...items, { text: answer[0]!, className: TEXT_VIEW }];
      // Written at an even pace over the whole generation time, so it grows until the end.
      const whole = answer.join("");
      const elapsed = this.options.now().getTime() - this.conversation.at;
      const written = Math.floor((whole.length * elapsed) / (this.options.generationMs ?? 20_000));
      return [
        ...items,
        { text: whole.slice(0, Math.max(1, written)), className: TEXT_VIEW },
        { id: id("msg_action_copy"), desc: "复制" },
        { id: id("msg_action_regenerate"), desc: "重新生成" },
      ];
    }
    const sourceEntry: MockNode[] =
      sources.length > 0
        ? [
            {
              id: id("ll_reference_title"),
              onTap: openSources,
              children: [
                {
                  id: id("tv_reference_title"),
                  className: TEXT_VIEW,
                  text: `搜索关键词，参考 ${sources.length} 篇资料`,
                  // A tap on the caption lands on the entry it belongs to.
                  onTap: openSources,
                },
              ],
            },
          ]
        : [];
    return [
      ...items,
      ...(this.options.sourceEntryAtEnd ? [] : sourceEntry),
      ...(this.options.tallAnswerRows
        ? [{ text: answer.join("\n"), className: TEXT_VIEW, rows: this.options.tallAnswerRows }]
        : answer.flatMap((text, index): MockNode[] => [
            { text, className: TEXT_VIEW },
            ...(this.options.inlineCopy && index === 0
              ? [
                  {
                    id: id("msg_action_copy"),
                    desc: "复制",
                    onTap: () => {
                      this.copied = "合成代码块";
                      this.copiedAt = this.options.now().getTime();
                    },
                  },
                ]
              : []),
          ])),
      ...(this.options.sourceEntryAtEnd ? sourceEntry : []),
      { id: id("risk_warning_content"), className: TEXT_VIEW, text: "内容由 AI 生成" },
      { id: id("time_text"), className: TEXT_VIEW, text: "12:00" },
      {
        id: id("msg_action_copy"),
        desc: "复制",
        onTap: () => {
          if (this.sent.length >= (this.options.copyDeadFrom ?? Number.POSITIVE_INFINITY)) return;
          this.copied = answer.join("\n");
          this.copiedAt = this.options.now().getTime();
        },
      },
      { id: id("msg_action_regenerate"), desc: "重新生成" },
      { id: id("topic_text"), className: TEXT_VIEW, text: "合成追问一" },
      { id: id("topic_text"), className: TEXT_VIEW, text: "合成追问二" },
      ...(this.options.followUps ?? []).map((text) => ({ text, className: TEXT_VIEW })),
    ];
  }
  // A source panel that was closed with the back key leaves the screen once its time is up.
  private settlePanel(): void {
    if (
      this.panelClosingAt !== null &&
      this.options.now().getTime() - this.panelClosingAt >= (this.options.panelCloseMs ?? 0)
    ) {
      this.panelClosingAt = null;
      if (this.view === "sources") this.leaveSources();
    }
  }
  // With `sourcePanelOverConversation`, the conversation under a sheet: what is under it can be
  // seen in the hierarchy, not tapped.
  private under(): MockNode[] {
    const inert = (nodes: MockNode[]): MockNode[] =>
      nodes.map((node) => ({
        ...node,
        onTap: undefined,
        onText: undefined,
        ...(node.children ? { children: inert(node.children) } : {}),
      }));
    return this.options.sourcePanelOverConversation ? inert(this.chat()) : [];
  }
  // Where the back key leads from the source list.
  private leaveSources(): void {
    this.view = this.toolSources !== null && !this.options.toolListExitsToChat ? "tools" : "chat";
    this.toolSources = null;
  }
  // The sources of the list that is open.
  private openList(): MockSource[] {
    return this.toolSources ?? this.options.citations ?? [];
  }
  private get equalCards(): boolean {
    return this.toolSources !== null || (this.options.equalCards ?? false);
  }
  // The message list row by row: an item of several rows is repeated, by reference.
  private messageRows(): MockNode[] {
    return this.messages().flatMap((item) => Array<MockNode>(item.rows ?? 1).fill(item));
  }
  // The card above the visible part of the source list, without its first line; of an equally
  // tall card, a lower one with its title only.
  private cutCard(sources: MockNode[]): MockNode[] {
    if (!(this.options.sourceCards || this.equalCards) || !this.options.cutTopCard) return [];
    const size = this.options.sourcesViewport ?? 50;
    const top = Math.min(this.sourcesTop, Math.max(0, sources.length - size));
    const above = sources[top - 1];
    if (!above) return [];
    return this.equalCards
      ? [{ ...above, height: CUT_CARD_HEIGHT, children: above.children!.slice(0, 1) }]
      : [{ ...above, children: above.children!.slice(1) }];
  }
  private window<T>(items: T[], top: number, size: number): T[] {
    return items.slice(Math.min(top, Math.max(0, items.length - size))).slice(0, size);
  }

  screen(): MockScreen {
    const launcher: MockScreen = {
      foreground: LAUNCHER,
      nodes: [{ id: `${LAUNCHER}:id/workspace`, desc: "主屏幕" }],
    };
    if (!this.running || !this.foregrounded) return launcher;
    this.settlePanel();
    if (this.startupDialogs > 0)
      return {
        foreground: "android",
        nodes: [
          {
            id: "android:id/aerr_close",
            text: "关闭应用",
            onTap: () => void (this.running = false),
          },
          {
            id: "android:id/aerr_wait",
            text: "等待",
            onTap: () => void (this.startupDialogs -= 1),
          },
        ],
      };
    if (this.startingReads > 0) {
      this.startingReads -= 1;
      return launcher;
    }
    if (this.options.crash === "after_send" && this.conversation) return launcher;
    if (this.options.anr && this.conversation)
      return {
        foreground: "android",
        nodes: [
          {
            id: "android:id/aerr_close",
            text: "关闭应用",
            onTap: () => void (this.running = false),
          },
          { id: "android:id/aerr_wait", text: "等待" },
        ],
      };
    const app = (nodes: MockNode[]): MockScreen => ({ foreground: MOCK_PACKAGE, nodes });
    if (this.applies(this.options.undeclaredDialog))
      return app([{ id: id("mock_unknown_dialog"), className: TEXT_VIEW, text: "合成未知弹窗" }]);
    if (this.applies(this.options.blocker?.when))
      return app([
        {
          login_required: { id: id("mock_login_wall"), text: "合成登录页" },
          challenge_required: { id: id("mock_verification"), text: "合成验证页" },
          rate_limited: { className: TEXT_VIEW, text: "合成提示：操作过于频繁" },
          emulator_rejected: { className: TEXT_VIEW, text: "合成提示：当前设备环境异常" },
        }[this.options.blocker!.code],
      ]);
    if (this.popupOpen)
      return app([
        { id: id("tv_first_guide_title"), className: TEXT_VIEW, text: "合成升级引导" },
        {
          id: id("tv_start_experience"),
          className: TEXT_VIEW,
          text: "我知道了",
          onTap: () => void (this.popupOpen = false),
        },
      ]);

    if (this.view === "sidebar")
      return app([
        ...(this.options.missing === "new_conversation"
          ? []
          : [
              {
                className: TEXT_VIEW,
                text: "新对话",
                onTap: () => {
                  this.conversation = null;
                  this.input = "";
                  this.top = 0;
                  this.view = "chat" as const;
                },
              },
            ]),
        { className: TEXT_VIEW, text: "合成历史对话" },
        this.options.loggedIn === false
          ? { className: TEXT_VIEW, text: "点击登录" }
          : { id: id("tv_username"), className: TEXT_VIEW, text: "合成用户" },
      ]);

    if (this.view === "tools")
      return app([
        ...this.under(),
        { className: TEXT_VIEW, text: "工具调用" },
        ...(this.options.toolSteps ?? []).map((step): MockNode => {
          const open = () => {
            if (!step.sources || this.options.toolRowDead) return;
            this.toolSources = step.sources;
            this.sourcesTop = 0;
            this.view = "sources";
          };
          // A tap on the step's text lands on the row it belongs to.
          return {
            onTap: open,
            children: [{ className: TEXT_VIEW, text: step.text, onTap: open }],
          };
        }),
      ]);

    if (this.view === "sources" && this.equalCards) {
      const sources = this.openList().map(
        (source): MockNode => ({
          onTap: () => undefined,
          height: source.cardHeight ?? CARD_HEIGHT,
          children: [
            { className: TEXT_VIEW, text: source.title },
            ...(source.site ? [{ className: TEXT_VIEW, text: source.site }] : []),
          ],
        }),
      );
      return app([
        { className: TEXT_VIEW, text: this.toolSources !== null ? "网页搜索" : "参考资料" },
        ...this.cutCard(sources),
        ...this.window(sources, this.sourcesTop, this.options.sourcesViewport ?? 50),
      ]);
    }

    if (this.view === "sources") {
      const cards = this.options.sourceCards ?? false;
      const sources = (this.options.citations ?? []).map(
        (source, index): MockNode => ({
          ...(cards ? { onTap: () => undefined } : {}),
          children: [
            { id: id("tv_reference_index"), className: TEXT_VIEW, text: String(index + 1) },
            { id: id("tv_reference_content"), className: TEXT_VIEW, text: source.title },
            ...(source.site
              ? [{ id: id("mock_reference_site"), className: TEXT_VIEW, text: source.site }]
              : []),
            ...(source.url
              ? [{ id: id("mock_reference_url"), className: TEXT_VIEW, text: source.url }]
              : []),
          ],
        }),
      );
      return app([
        ...this.under(),
        {
          id: id("bottom_sheet_title"),
          className: TEXT_VIEW,
          text:
            this.options.statedSources === undefined
              ? "参考资料"
              : `参考资料 ${this.options.statedSources}`,
        },
        ...(this.options.missing === "sources_close"
          ? []
          : [
              {
                id: id("bottom_sheet_close"),
                desc: "关闭",
                onTap: () => void (this.view = "chat"),
              },
            ]),
        ...(this.options.queries ? [{ className: TEXT_VIEW, text: this.options.queries }] : []),
        // Only at the top of the list: this row scrolls away with it.
        ...(this.options.statedSourcesInList && this.sourcesTop === 0
          ? [
              {
                id: id("mock_reference_count"),
                className: TEXT_VIEW,
                text: `参考 ${this.options.statedSources ?? sources.length} 篇资料`,
              },
            ]
          : []),
        ...this.cutCard(sources),
        ...this.window(sources, this.sourcesTop, this.options.sourcesViewport ?? 50),
      ]);
    }

    return app(this.chat());
  }

  // The conversation screen: top bar, message list, jump-to-end control and composer.
  private chat(): MockNode[] {
    const messages = this.messageRows();
    const viewport = this.options.viewport ?? 50;
    const atEnd = this.top >= messages.length - viewport;
    return [
      { id: id("back_icon"), desc: "打开侧边栏", onTap: () => void (this.view = "sidebar") },
      ...(this.conversation
        ? [
            {
              id: id("message_list"),
              // A tall item shows once, however many of its rows are on screen.
              children: [...new Set(this.window(messages, this.top, viewport))],
            },
          ]
        : [{ id: id("larus_mode_switch_welcome_title"), className: TEXT_VIEW, text: "你好" }]),
      ...(this.conversation && !atEnd
        ? [
            {
              id: id("fast_button_icon"),
              desc: "回到底部",
              onTap: () => void (this.top = Math.max(0, this.messageRows().length - viewport)),
            },
          ]
        : []),
      this.mode === "voice"
        ? { id: id("action_input"), desc: "切换到文本输入", onTap: () => void (this.mode = "text") }
        : {
            id: id("input_text"),
            className: "android.widget.EditText",
            text: this.input,
            onText: (text: string) =>
              void (this.input = this.options.composerDropsBlankLines
                ? text.replaceAll("\n\n", "\n")
                : text),
          },
      ...(this.mode === "text" && this.input !== "" && this.options.missing !== "send"
        ? [
            {
              id: id("action_send"),
              desc: "发送",
              onTap: () => {
                this.sent.push(this.input);
                this.conversation = { text: this.input, at: this.options.now().getTime() };
                this.input = "";
                this.top = 0;
              },
            },
          ]
        : []),
    ];
  }

  activate(packageName: string): void {
    if (packageName !== MOCK_PACKAGE || this.options.crash === "launch") return;
    if (!this.running) {
      this.view = "chat";
      this.toolSources = null;
      this.mode = "voice";
      this.startingReads = this.options.slowStartReads ?? 0;
      this.startupDialogs = this.options.startupAnr ?? 0;
    }
    this.running = true;
    this.foregrounded = true;
  }
  terminate(packageName: string): void {
    if (packageName === MOCK_PACKAGE) this.running = false;
  }
  back(): void {
    this.settlePanel();
    if (this.view === "chat") this.foregrounded = false;
    else if (this.view === "sources") {
      // A press while the panel is on its way out changes nothing.
      if (this.panelClosingAt !== null) return;
      this.panelBacks += 1;
      if (this.panelBacks >= (this.options.sourcePanelBacks ?? 1)) {
        this.panelBacks = 0;
        if (this.options.panelCloseMs) this.panelClosingAt = this.options.now().getTime();
        else this.leaveSources();
      }
    } else if (this.view === "tools") {
      this.toolBacks += 1;
      if (this.toolBacks >= (this.options.toolScreenBacks ?? 1)) {
        this.toolBacks = 0;
        this.view = "chat";
        // The conversation comes back at the start of the answer.
        this.top = 0;
      }
    } else this.view = "chat";
  }
  home(): void {
    this.foregrounded = false;
  }
  swipe(direction: SwipeDirection): void {
    const move = (top: number, length: number, size: number, page = false) => {
      const step = page ? size : Math.max(1, size - 2);
      return direction === "up"
        ? Math.min(Math.max(0, length - size), top + step)
        : Math.max(0, top - step);
    };
    if (this.view === "sources")
      this.sourcesTop = move(
        this.sourcesTop,
        this.openList().length,
        this.options.sourcesViewport ?? 50,
        this.options.sourcesPageSwipe,
      );
    else if (this.view === "chat")
      this.top = move(this.top, this.messageRows().length, this.options.viewport ?? 50);
  }
  clipboard(): string {
    return this.copied;
  }
}

export interface MockIconSource {
  title: string;
  // Lines between title and link, as a page's own text has them.
  snippet?: string[];
  // A source without a link line, where set to null.
  url: string | null;
}
export interface MockIconAppOptions {
  now: () => Date;
  generationMs?: number;
  answer?: string[];
  citations?: MockIconSource[];
  // The number the source entry states, where it is not the number of sources.
  statedSources?: number;
  sourcesViewport?: number;
  loggedIn?: boolean;
  // The App is opened on a conversation that already has an answer.
  startInConversation?: boolean;
  // With `startInConversation`: that conversation is open somewhere in the middle of its
  // answer. What follows the answer text (source entry, the row of icons, follow-ups) is below
  // the screen and, as in an App that draws its own screen, not in the hierarchy until a swipe
  // brings it there.
  endBelowScreen?: boolean;
  // The hint above the composer stays on screen this long after a text was typed.
  hintLingersMs?: number;
}
const IMAGE_VIEW = "android.widget.ImageView";

// A chat App that draws its own screen, shaped like the real case of design §16.8: no view has
// a resource ID, every text is a content description, and the controls (menu, new conversation,
// send, copy, close) are image views without any label. Only the input is a native text field.
// Icons that sit side by side share a row of the screen.
export class MockIconApp implements MockModel {
  readonly sent: string[] = [];
  // Taps that landed on a control the flow has no business with.
  readonly strayTaps: string[] = [];
  private running = false;
  private foregrounded = false;
  private view: "chat" | "sidebar" | "sources" = "chat";
  private input = "";
  private conversation: { text: string; at: number } | null = null;
  private sourcesTop = 0;
  private copied = "";
  private endBelow = false;
  // The input has the keyboard. Only then does a text that is set reach the App: without it
  // the field's view reports the text it was given while the App still shows its placeholder
  // (real device, design §16.8).
  private focused = false;
  // What the field's view reports.
  private reported = "";
  private typedAt = Number.NEGATIVE_INFINITY;

  constructor(private readonly options: MockIconAppOptions) {
    if (options.startInConversation) {
      this.conversation = { text: "合成旧问题", at: Number.NEGATIVE_INFINITY };
      this.endBelow = options.endBelowScreen ?? false;
    }
  }

  private get complete(): boolean {
    return (
      this.conversation !== null &&
      this.options.now().getTime() - this.conversation.at >= (this.options.generationMs ?? 20_000)
    );
  }
  private answer(): string[] {
    return this.options.answer ?? ["合成回答第一段", "合成回答第二段"];
  }
  private icon(name: string, onTap?: () => void, beside = false): MockNode {
    return {
      className: IMAGE_VIEW,
      onTap: onTap ?? (() => void this.strayTaps.push(name)),
      ...(beside ? { beside } : {}),
    };
  }
  private send(): void {
    // A text sent on the home screen starts a conversation; one sent anywhere else goes into
    // the conversation that is open, after what it already holds.
    if (this.conversation) this.strayTaps.push("send_in_conversation");
    this.sent.push(this.input);
    this.conversation = { text: this.input, at: this.options.now().getTime() };
    this.clearInput();
    this.endBelow = false;
  }
  private clearInput(): void {
    this.input = "";
    this.reported = "";
    this.focused = false;
  }
  private field(): MockNode {
    return {
      className: "android.widget.EditText",
      text: this.reported,
      onTap: () => void (this.focused = true),
      onText: (text) => {
        this.reported = text;
        if (!this.focused) return;
        this.input = text;
        this.typedAt = this.options.now().getTime();
      },
    };
  }
  // The icon at the right of the composer. With a text in the App it sends, and does not say
  // that it takes a tap; without one it switches to voice input, and says so.
  private sendOrVoice(): MockNode {
    return this.input !== ""
      ? { className: IMAGE_VIEW, onTap: () => this.send(), clickable: false, beside: true }
      : this.icon("voice", undefined, true);
  }
  // Above the composer of the home screen: a picture while the input is not in use, and a hint
  // with two small pictures of its own while no text is typed. Neither picture takes a tap.
  private homeExtras(): MockNode[] {
    const hint =
      this.input === "" ||
      this.options.now().getTime() - this.typedAt < (this.options.hintLingersMs ?? 0);
    return [
      ...(this.focused ? [] : [{ className: IMAGE_VIEW }, { desc: "合成问候语" }]),
      ...(hint
        ? [
            {
              desc: "示例",
              onTap: () => void this.strayTaps.push("hint"),
              children: [{ className: IMAGE_VIEW }, { className: IMAGE_VIEW }],
            },
          ]
        : []),
    ];
  }

  screen(): MockScreen {
    if (!this.running || !this.foregrounded)
      return { foreground: LAUNCHER, nodes: [{ id: `${LAUNCHER}:id/workspace`, desc: "主屏幕" }] };
    const app = (nodes: MockNode[]): MockScreen => ({ foreground: MOCK_PACKAGE, nodes });
    const sources = this.options.citations ?? [];

    // The sidebar is the one native screen: its views have resource IDs.
    if (this.view === "sidebar")
      return app([
        { id: id("iv_sidebar_avatar"), className: IMAGE_VIEW, onTap: () => undefined },
        ...(this.options.loggedIn === false
          ? []
          : [{ id: id("tv_sidebar_name"), className: TEXT_VIEW, text: "合成用户" }]),
        {
          id: id("iv_sidebar_close"),
          className: IMAGE_VIEW,
          onTap: () => void (this.view = "chat"),
        },
      ]);

    if (this.view === "sources") {
      const size = this.options.sourcesViewport ?? 50;
      const cards = sources.map(
        (source): MockNode => ({
          desc: [
            source.title,
            ...(source.snippet ?? ["..."]),
            "知",
            ...(source.url ? [source.url] : []),
          ].join("\n"),
          onTap: () => void this.strayTaps.push("source"),
        }),
      );
      return app([
        { desc: "纱罩", onTap: () => void (this.view = "chat") },
        this.icon("close", () => void (this.view = "chat")),
        { desc: "参考资料" },
        ...cards.slice(Math.min(this.sourcesTop, Math.max(0, cards.length - size))).slice(0, size),
      ]);
    }

    const top: MockNode[] = [
      this.icon("menu", () => void (this.view = "sidebar")),
      {
        className: IMAGE_VIEW,
        desc: "合成模型\n快速",
        onTap: () => void this.strayTaps.push("model"),
      },
    ];
    // Home: an empty conversation. Its composer has the mode switch, and its two icons share a
    // row, as the two icons of the top bar of a conversation do.
    if (!this.conversation)
      return app([
        ...top,
        ...this.homeExtras(),
        this.field(),
        // One row: the attach icon, the mode switch and the voice/send icon.
        this.icon("attach"),
        { desc: "对话", onTap: () => undefined, beside: true },
        { desc: "任务", onTap: () => void this.strayTaps.push("task_mode"), beside: true },
        this.sendOrVoice(),
      ]);

    const stated = this.options.statedSources ?? sources.length;
    // What follows the text of a finished answer.
    const below: MockNode[] = [
      // A table's own pair of icons, inside the answer.
      this.icon("table_copy"),
      this.icon("table_expand", undefined, true),
      // A table cell that reads like the mode switch of the home screen; it takes no tap.
      { desc: "任务" },
      ...(sources.length > 0
        ? [
            {
              desc: `${stated}个来源`,
              onTap: () => {
                this.view = "sources" as const;
                this.sourcesTop = 0;
              },
            },
          ]
        : []),
      this.icon("copy", () => void (this.copied = this.answer().join("\n"))),
      this.icon("speak", undefined, true),
      this.icon("share", undefined, true),
      { desc: "合成追问一", onTap: () => void this.strayTaps.push("follow_up") },
    ];
    return app([
      ...top,
      this.icon("new", () => {
        this.conversation = null;
        this.clearInput();
        this.endBelow = false;
      }),
      this.icon("more", undefined, true),
      { desc: this.conversation.text },
      ...(this.complete
        ? [
            ...this.answer().map((text): MockNode => ({ desc: text, onTap: () => undefined })),
            ...(this.endBelow ? [] : below),
          ]
        : [{ desc: this.answer()[0]!, onTap: () => undefined }]),
      // One row: the attach icon, the input and the voice/send icon.
      this.icon("attach"),
      { ...this.field(), beside: true },
      this.sendOrVoice(),
    ]);
  }

  activate(packageName: string): void {
    if (packageName !== MOCK_PACKAGE) return;
    if (!this.running) this.view = "chat";
    this.running = true;
    this.foregrounded = true;
  }
  terminate(packageName: string): void {
    if (packageName === MOCK_PACKAGE) this.running = false;
  }
  back(): void {
    if (this.view === "chat") this.foregrounded = false;
    else this.view = "chat";
  }
  home(): void {
    this.foregrounded = false;
  }
  swipe(direction: SwipeDirection): void {
    // A swipe up on a conversation brings the end of its answer on screen.
    if (this.view === "chat" && direction === "up") this.endBelow = false;
    if (this.view !== "sources") return;
    const size = this.options.sourcesViewport ?? 50;
    const length = (this.options.citations ?? []).length;
    const step = Math.max(1, size - 2);
    this.sourcesTop =
      direction === "up"
        ? Math.min(Math.max(0, length - size), this.sourcesTop + step)
        : Math.max(0, this.sourcesTop - step);
  }
  clipboard(): string {
    return this.copied;
  }
}
