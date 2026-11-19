import { inRow, isUnlabeled, matchesTextPattern, type Selector } from "./driver.ts";

// The UI hierarchy as the automation driver exports it (UiAutomator XML: one element per view,
// the tag being the class name, with `text`, `content-desc`, `resource-id` and `bounds`
// attributes). Answers and sources are read from one parsed export instead of many element
// lookups, and the mock driver renders its scripted screens through the same matcher.

export interface Bounds {
  left: number;
  top: number;
  right: number;
  bottom: number;
}
export interface UiNode {
  className: string;
  // Exactly as the hierarchy shows it: package-prefixed ("pkg:id/name") or a bare test tag.
  resourceId: string;
  text: string;
  desc: string;
  packageName: string;
  // The view reacts to a tap. Used to tell the cards of an ID-less list apart (cards.ts).
  clickable: boolean;
  bounds: Bounds | null;
  parent: UiNode | null;
  children: UiNode[];
}

const TAG = /<(\/?)([^\s<>/!?][^\s<>/]*)((?:\s+[^\s=<>/]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>/gu;
const ATTRIBUTE = /([^\s=]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/gu;
const ENTITIES: Record<string, string> = {
  amp: "&",
  lt: "<",
  gt: ">",
  quot: '"',
  apos: "'",
};

function decode(value: string): string {
  return value.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/giu, (whole, name: string) => {
    if (name.startsWith("#x") || name.startsWith("#X"))
      return String.fromCodePoint(Number.parseInt(name.slice(2), 16));
    if (name.startsWith("#")) return String.fromCodePoint(Number.parseInt(name.slice(1), 10));
    return ENTITIES[name.toLowerCase()] ?? whole;
  });
}
export function parseBounds(value: string | undefined): Bounds | null {
  const match = /^\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]$/u.exec(value ?? "");
  if (!match) return null;
  const [left, top, right, bottom] = match.slice(1).map(Number) as [number, number, number, number];
  return { left, top, right, bottom };
}

// Parses an exported hierarchy. The returned root is the document itself; an export without a
// single view (seen right after a cold start) gives a root without children. Throws on text that
// is not a well-formed export, which callers treat like an unreadable hierarchy.
export function parseHierarchy(xml: string): UiNode {
  const root: UiNode = {
    className: "hierarchy",
    resourceId: "",
    text: "",
    desc: "",
    packageName: "",
    clickable: false,
    bounds: null,
    parent: null,
    children: [],
  };
  let current = root;
  let sawDocument = false;
  for (const match of xml.matchAll(TAG)) {
    const [, closing, tag, rawAttributes, selfClosing] = match;
    if (tag === "hierarchy") {
      sawDocument = true;
      continue;
    }
    if (closing) {
      if (current.parent === null) throw new Error("hierarchy_invalid");
      current = current.parent;
      continue;
    }
    const attributes: Record<string, string> = {};
    for (const attribute of (rawAttributes ?? "").matchAll(ATTRIBUTE))
      attributes[attribute[1]!] = decode(attribute[2] ?? attribute[3] ?? "");
    const node: UiNode = {
      className: attributes.class ?? tag!,
      resourceId: attributes["resource-id"] ?? "",
      text: attributes.text ?? "",
      desc: attributes["content-desc"] ?? "",
      packageName: attributes.package ?? "",
      clickable: attributes.clickable === "true",
      bounds: parseBounds(attributes.bounds),
      parent: current,
      children: [],
    };
    current.children.push(node);
    if (!selfClosing) current = node;
  }
  if (!sawDocument || current !== root) throw new Error("hierarchy_invalid");
  return root;
}

// Every view below `root` in document order (top to bottom as the screen lists them).
export function* descendants(root: UiNode): Generator<UiNode> {
  for (const child of root.children) {
    yield child;
    yield* descendants(child);
  }
}

// All given conditions must hold. `pick` is not a condition, and neither is `row` here: it
// compares a view with the other matches of a screen, which `findNodes` does.
export function matchesSelector(
  // A view that does not say whether it reacts to a tap meets no `clickable` condition.
  node: Pick<UiNode, "resourceId" | "text" | "desc" | "className"> & { clickable?: boolean },
  selector: Selector,
): boolean {
  return (
    (selector.id === undefined || node.resourceId === selector.id) &&
    (selector.text === undefined || node.text === selector.text) &&
    (selector.textContains === undefined || node.text.includes(selector.textContains)) &&
    (selector.textMatches === undefined || matchesTextPattern(node.text, selector.textMatches)) &&
    (selector.desc === undefined || node.desc === selector.desc) &&
    (selector.descContains === undefined || node.desc.includes(selector.descContains)) &&
    (selector.descMatches === undefined || matchesTextPattern(node.desc, selector.descMatches)) &&
    (selector.className === undefined || node.className === selector.className) &&
    (selector.clickable === undefined || node.clickable === selector.clickable) &&
    (selector.unlabeled !== true || isUnlabeled(node))
  );
}
export function findNodes(root: UiNode, selector: Selector): UiNode[] {
  return inRow(
    [...descendants(root)].filter((node) => matchesSelector(node, selector)),
    selector,
  );
}
export function isInside(node: UiNode, ancestor: UiNode): boolean {
  for (let parent = node.parent; parent !== null; parent = parent.parent)
    if (parent === ancestor) return true;
  return false;
}

// A hierarchy worth acting on: some view carries text, a description or an identifier. Two empty
// frames (the first read of some Apps) do not count.
export function isReadable(root: UiNode): boolean {
  for (const node of descendants(root))
    if (node.text !== "" || node.desc !== "" || node.resourceId !== "") return true;
  return false;
}

// Joins two reads of one scrolling list. `earlier` was read after scrolling towards the start, so
// its tail repeats the head of `later`; the longest such repetition is dropped once.
export function mergeOverlap<T>(
  earlier: readonly T[],
  later: readonly T[],
  same: (a: T, b: T) => boolean = Object.is,
): T[] {
  for (let size = Math.min(earlier.length, later.length); size > 0; size -= 1) {
    const tail = earlier.slice(earlier.length - size);
    if (tail.every((item, index) => same(item, later[index]!)))
      return [...earlier, ...later.slice(size)];
  }
  return [...earlier, ...later];
}
// What the screen shows and where: identifier, text, description and bounds of every view. Two
// reads with the same signature mean nothing moved and nothing changed. Positions are part of
// it because an App that does not expose its answer text still moves and grows its views while
// the answer is written, and because a swipe moves views whose texts stay the same.
export function layoutSignature(root: UiNode): string {
  return JSON.stringify(
    [...descendants(root)].map((node) => [node.resourceId, node.text, node.desc, node.bounds]),
  );
}
// `mergeOverlap` for lists whose windows can carry a misread item at an edge (a source card cut
// by the top of the list can look like another card). When the windows do not overlap exactly,
// they are joined at the last earlier item the later window also shows: what the later window
// has before it is dropped, what the earlier list has after it is replaced.
//
// Such a join must be backed by the items before it: everything the later window shows between
// its first item (the one that may be misread) and the join has to repeat the earlier list. An
// item that merely shows up again (the same source listed twice) is therefore not taken for the
// join, which would drop what lies between or what the earlier list already holds. Of several
// possible joins the one with the longest agreement wins, as with an exact overlap. Windows
// without such a join are appended, as before.
export function mergeWindows<T>(
  earlier: readonly T[],
  later: readonly T[],
  same: (a: T, b: T) => boolean = Object.is,
): T[] {
  for (let size = Math.min(earlier.length, later.length); size > 0; size -= 1) {
    const tail = earlier.slice(earlier.length - size);
    if (tail.every((item, index) => same(item, later[index]!)))
      return [...earlier, ...later.slice(size)];
  }
  for (let index = earlier.length - 1; index >= 0; index -= 1)
    for (let at = later.length - 1; at >= 0; at -= 1) {
      if (!same(earlier[index]!, later[at]!)) continue;
      let agrees = true;
      for (let back = 1; agrees && back < at && back <= index; back += 1)
        agrees = same(earlier[index - back]!, later[at - back]!);
      if (agrees) return [...earlier.slice(0, index + 1), ...later.slice(at + 1)];
    }
  return [...earlier, ...later];
}
