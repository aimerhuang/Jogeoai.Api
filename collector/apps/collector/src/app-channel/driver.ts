import { z } from "zod";
import type { Bounds } from "./hierarchy.ts";

// Device automation as the collection flow sees it (design §2). The flow, the login probe and
// the scheduler depend only on this interface: tests use the scripted mock driver and the real
// driver stays a thin translation to Appium. Nothing here knows an Activity name.

const condition = z.string().min(1).max(300);
// Which views a contract step means. Every given condition must hold; at least one is required.
// Several Apps expose no resource IDs, so text, description and class are first-class.
//   id            the full resource-id string exactly as the hierarchy shows it: package-prefixed
//                 ("com.example:id/input") or a bare Compose test tag ("chat_new")
//   text          the view's text, exact
//   textContains  a substring of the view's text
//   textMatches   a regular expression the whole text must match, for a text with a changing
//                 part ("使用 \\d+ 个工具.*" in JSON). On the device it is UiSelector's
//                 `textMatches`: a Java pattern, with "." also matching a line break. Exported
//                 hierarchies are matched the same way; keep to syntax that means the same in
//                 Java and JavaScript. The class shorthands do not for text outside ASCII:
//                 on Android "\\d" and "\\w" also take full-width digits and Chinese
//                 characters, and "\\b" sees no boundary between Chinese and Latin text; here
//                 they are ASCII only. Write "[0-9]" and literal text where that matters.
//   desc          the content description, exact
//   descContains  a substring of the content description
//   descMatches   as `textMatches`, for the content description. An App that draws its own
//                 screen puts every text there, the answer's included, so a substring would
//                 also be found inside a sentence of the answer.
//   className     the view class, exact ("android.widget.EditText")
//   clickable     whether the view reacts to a tap; only together with another condition
//
// Two more conditions tell apart controls that carry no label at all (real device, Zhipu
// Qingyan 3.8.7: copy, new conversation and send are bare image views). Both need `className`.
//   unlabeled     true: the view has no text, no content description and no resource ID
//   row           { size, at }: of the views meeting the other conditions, those with the same
//                 top and bottom edge form a row; the view is the `at`-th from the left (0-based)
//                 of a row of exactly `size`. One view more or fewer and nothing matches.
//
// `pick` is not a condition. It says which match to act on when several are visible; the default
// "only" makes more than one visible match a contract change instead of a guess.
export const SELECTOR_CONDITIONS = [
  "id",
  "text",
  "textContains",
  "textMatches",
  "desc",
  "descContains",
  "descMatches",
  "className",
  "clickable",
] as const;
// The conditions that give a view a label; `unlabeled` contradicts each of them.
const LABEL_CONDITIONS = [
  "id",
  "text",
  "textContains",
  "textMatches",
  "desc",
  "descContains",
  "descMatches",
] as const;
// The flags a `textMatches` pattern is compiled with wherever it is used: Unicode, and "." also
// matching a line break, as on the device.
const TEXT_PATTERN_FLAGS = "su";
// Whether the whole of `text` matches a `textMatches` pattern.
export function matchesTextPattern(text: string, pattern: string): boolean {
  return new RegExp(`^(?:${pattern})$`, TEXT_PATTERN_FLAGS).test(text);
}
const textPattern = condition.refine((pattern) => {
  // The expression sent to the device ends the pattern with a quote, and a backslash before
  // that quote would escape it.
  if (pattern.endsWith("\\")) return false;
  try {
    matchesTextPattern("", pattern);
    return true;
  } catch {
    return false;
  }
}, "is not a valid regular expression");
const selectorShape = z
  .object({
    id: condition.optional(),
    text: condition.optional(),
    textContains: condition.optional(),
    textMatches: textPattern.optional(),
    desc: condition.optional(),
    descContains: condition.optional(),
    descMatches: textPattern.optional(),
    className: condition.optional(),
    clickable: z.boolean().optional(),
    unlabeled: z.literal(true).optional(),
    row: z
      .object({ size: z.number().int().min(2).max(6), at: z.number().int().min(0).max(5) })
      .strict()
      .refine((row) => row.at < row.size, "at must be less than size")
      .optional(),
    pick: z.enum(["only", "first", "last"]).optional(),
  })
  .strict();
type SelectorShape = z.infer<typeof selectorShape>;
const selectorRules = <T extends z.ZodType<SelectorShape>>(schema: T) =>
  schema
    // `clickable` narrows a selector and is never its only condition: alone it would name every
    // view of a screen that takes a tap, or every one that does not.
    .refine(
      (selector) =>
        selector.unlabeled === true ||
        SELECTOR_CONDITIONS.some((key) => key !== "clickable" && selector[key] !== undefined),
      "needs at least one condition",
    )
    // Without a class the device would be asked for every view of the screen.
    .refine(
      (selector) =>
        (selector.unlabeled === undefined && selector.row === undefined) ||
        selector.className !== undefined,
      "unlabeled and row need className",
    )
    .refine(
      (selector) =>
        selector.unlabeled === undefined ||
        LABEL_CONDITIONS.every((key) => selector[key] === undefined),
      "unlabeled cannot be combined with an id, text or description condition",
    );
export const selectorSchema = selectorRules(selectorShape);
export type Selector = z.infer<typeof selectorSchema>;
// A selector for the places that ask one view at a time whether it matches: `row` is a filter
// over the views of a screen and cannot be answered there.
export const viewSelectorSchema = selectorRules(selectorShape).refine(
  (selector) => selector.row === undefined,
  "row cannot be used where views are compared one by one",
);

// What the label and row conditions look at. Both a view of an exported hierarchy and an element
// a lookup returned have these.
interface Labelled {
  text: string;
  desc: string;
  resourceId: string;
  bounds: Bounds | null;
}
export function isUnlabeled(view: Pick<Labelled, "text" | "desc" | "resourceId">): boolean {
  return view.text === "" && view.desc === "" && view.resourceId === "";
}
// The `row` condition: of `views`, which already meet every other condition of `selector` and
// are in screen order, those that are the `at`-th from the left of a row of exactly `size`.
// Without the condition all of them. The order of `views` is kept.
//
// A view without bounds is in no row, and neither is one that takes up no room: views scrolled
// out of their list can still be reported, all with the same empty box, and would pass for a
// row at that place.
export function inRow<T extends Pick<Labelled, "bounds">>(
  views: readonly T[],
  selector: Pick<Selector, "row">,
): T[] {
  const { row } = selector;
  if (row === undefined) return [...views];
  const rows = new Map<string, T[]>();
  for (const view of views) {
    if (!view.bounds) continue;
    if (view.bounds.right <= view.bounds.left || view.bounds.bottom <= view.bounds.top) continue;
    const key = `${view.bounds.top}:${view.bounds.bottom}`;
    rows.set(key, [...(rows.get(key) ?? []), view]);
  }
  const chosen = new Set<T>();
  for (const members of rows.values()) {
    if (members.length !== row.size) continue;
    chosen.add([...members].sort((a, b) => a.bounds!.left - b.bounds!.left)[row.at]!);
  }
  return views.filter((view) => chosen.has(view));
}

// A visible view found by a selector. `ref` is only meaningful to the driver that returned it.
export interface UiElement {
  ref: string;
  text: string;
  desc: string;
  className: string;
  resourceId: string;
  bounds: Bounds | null;
}

// Direction the finger moves: "up" brings later content on screen, "down" earlier content.
export type SwipeDirection = "up" | "down";
// One gesture call never swipes more often than this; callers bound their own totals as well.
export const MAX_SWIPES_PER_CALL = 10;

export interface DeviceDriver {
  // Brings the App to the foreground, starting it when needed. Never clears its data.
  activateApp(packageName: string): Promise<void>;
  terminateApp(packageName: string): Promise<void>;
  // Package of the focused window; null when it cannot be told.
  foregroundPackage(): Promise<string | null>;
  // Visible views matching every condition, in screen order. A targeted lookup, not a hierarchy
  // export: this is what polling uses.
  find(selector: Selector): Promise<UiElement[]>;
  tap(element: UiElement, beforeClick?: () => Promise<void>): Promise<void>;
  // Replaces the content of an input with exactly `text`, as one value (no per-key timing).
  setText(element: UiElement, text: string): Promise<void>;
  readText(element: UiElement): Promise<string>;
  // `count` plain swipe gestures (1..MAX_SWIPES_PER_CALL) inside `within`, or the middle of the
  // screen. This is the only way to scroll: no scroll-until-found helper exists.
  swipe(direction: SwipeDirection, count: number, within?: Bounds): Promise<void>;
  back(): Promise<void>;
  home(): Promise<void>;
  // The whole UI hierarchy as XML (see hierarchy.ts). Costly for the App; not for polling.
  hierarchy(): Promise<string>;
  // PNG of the screen written to `path` (owner-only).
  screenshot(path: string): Promise<void>;
  readClipboard(): Promise<string>;
  // Ends the automation session. The App and its data stay as they are.
  close(): Promise<void>;
}

// A driver command that did not do what was asked. The flow decides what it means for the
// attempt (device gone, App gone, or the screen changed under the step).
export class DriverError extends Error {
  constructor(
    readonly kind: "unreachable" | "stale_element" | "command_failed",
    // For logs only: the driver method that failed and the protocol's error code. Both are
    // fixed vocabularies; a driver's free-text message never gets here, it may quote App text.
    readonly command?: string,
    readonly errorName?: string,
  ) {
    super(kind);
    this.name = "DriverError";
  }
}

export interface DriverTools {
  // Tool name -> version, for batch.json.
  tools: Record<string, string>;
  // Packages the driver installs on a device.
  helperPackages: string[];
}
// Opens one automation session per device at a time. `serial` comes from `DeviceInspector`.
export interface DriverProvider {
  open(target: { deviceId: string; serial: string }): Promise<DeviceDriver>;
  describe(): Promise<DriverTools>;
  // Ends everything the provider started (sessions, the automation server).
  close(): Promise<void>;
}
