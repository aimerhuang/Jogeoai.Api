import type { Locator } from "playwright";

// Answer body DOM → Markdown (R8). The frontend renders answers with marked (GFM, breaks: false)
// and shows raw HTML as literal text, so the output is plain CommonMark/GFM only.
//
// `answerToMarkdown` runs inside the page through Playwright `locator.evaluate`, which serializes
// the function source: it must not use imports or module-scope bindings at runtime. Every helper
// is an object-literal method on purpose. The live collector runs under tsx, whose esbuild
// `keepNames` wraps nested function declarations and `const f = () => …` in a module-scope
// `__name(...)` call that does not exist in the page (guarded by answer-markdown.test.ts).

/** Minimal structural DOM types: the project tsconfig has no DOM lib. */
export interface AnswerNode {
  readonly nodeType: number;
  readonly nodeValue: string | null;
  readonly childNodes: ArrayLike<AnswerNode>;
  readonly baseURI: string;
}
export interface AnswerElement extends AnswerNode {
  readonly tagName: string;
  readonly ownerDocument: {
    readonly defaultView: {
      getComputedStyle(element: AnswerElement): { readonly display: string };
    } | null;
  } | null;
  getAttribute(name: string): string | null;
  matches(selector: string): boolean;
}

// Inline tokens. "c" is one character unit (possibly backslash-escaped) classified like marked's
// flanking rules: "p" = Unicode punctuation or symbol, "w" = anything else. "a" is an opaque atom
// (link or code span, starts and ends with punctuation). "d" is an emphasis delimiter.
type InlineToken =
  | { k: "c"; s: string; c: "w" | "p" }
  | { k: "a"; s: string }
  | { k: "sp" }
  | { k: "br" }
  | { k: "d"; open: boolean; mark: "**" | "*" };
interface MarkdownBlock {
  k: "p" | "h" | "list" | "quote" | "code" | "table" | "hr";
  s: string;
  ordered?: boolean;
  items?: { n: number; s: string }[];
}
interface InlineContext {
  strong: boolean;
  em: boolean;
  cell: boolean;
}
type FinishMode = "block" | "inline" | "heading" | "cell";

/**
 * Converts a rendered answer body to Markdown (design.md §6). Pure and page-evaluable:
 * `locator.evaluate(answerToMarkdown, contract.selectors.answerIgnore)`.
 *
 * - Blocks: h1–h6, p/div paragraphs, ul/ol/li (nested, `start`/`value` honoured), blockquote,
 *   GFM tables, pre (fenced), hr. Inline: strong/b, em/i, code, http(s) links, br; an empty block
 *   (Doubao's `<div></div>`) inside inline content is a hard break.
 * - Dropped: media, script/style, buttons, form controls, `display: none` and anything matching
 *   `ignoreSelector` (an invalid selector disables ignoring instead of throwing).
 * - Text is whitespace-collapsed and escaped (`\ * _ ~ \` [ ] |` everywhere, entity-like `&`,
 *   and `# > - + < =` / `1.` / `1)` at the start of a line); code is not escaped.
 * - `**`/`*` never sit between CJK punctuation and a letter where CommonMark could not close
 *   them: the edge punctuation moves outside the delimiters instead (`**A（B**）和`).
 */
export function answerToMarkdown(root: AnswerElement, ignoreSelector?: string): string {
  const DROPPED = new Set([
    "img",
    "picture",
    "svg",
    "video",
    "audio",
    "canvas",
    "script",
    "style",
    "button",
    "noscript",
    "template",
    "iframe",
    "object",
    "embed",
    "input",
    "textarea",
    "select",
  ]);
  const BLOCKS = new Set([
    "address",
    "article",
    "aside",
    "blockquote",
    "caption",
    "center",
    "dd",
    "details",
    "dialog",
    "div",
    "dl",
    "dt",
    "fieldset",
    "figcaption",
    "figure",
    "footer",
    "form",
    "h1",
    "h2",
    "h3",
    "h4",
    "h5",
    "h6",
    "header",
    "hgroup",
    "hr",
    "li",
    "main",
    "menu",
    "nav",
    "ol",
    "p",
    "pre",
    "section",
    "summary",
    "table",
    "tbody",
    "td",
    "tfoot",
    "th",
    "thead",
    "tr",
    "ul",
  ]);
  const ESCAPED = /[\\`*_~[\]|]/u;
  const PUNCTUATION = /[\p{P}\p{S}]/u;
  const SPACE = /\s/u;
  const INVISIBLE = /[\u00AD\u200B\u2060\uFEFF]/gu;
  const ENTITY = /&(?=#\d{1,7};|#[xX][\da-fA-F]{1,6};|[A-Za-z][A-Za-z\d]{1,31};)/gu;
  const MAX_NUMBER = 999_999_999;
  let ignore = ignoreSelector?.trim() || undefined;
  if (ignore) {
    try {
      root.matches(ignore);
    } catch {
      ignore = undefined;
    }
  }
  const view = root.ownerDocument?.defaultView ?? null;
  const hiddenCache = new Map<AnswerElement, boolean>();

  const h = {
    tag(element: AnswerElement): string {
      return element.tagName.toLowerCase();
    },
    children(node: AnswerNode): AnswerNode[] {
      return Array.from(node.childNodes);
    },
    hidden(element: AnswerElement): boolean {
      let hidden = hiddenCache.get(element);
      if (hidden === undefined) {
        hidden = DROPPED.has(h.tag(element));
        if (!hidden && ignore) {
          try {
            hidden = element.matches(ignore);
          } catch {
            hidden = false;
          }
        }
        if (!hidden && view) {
          try {
            hidden = view.getComputedStyle(element).display === "none";
          } catch {
            hidden = false;
          }
        }
        hiddenCache.set(element, hidden);
      }
      return hidden;
    },
    // No visible text after dropping media/ignored nodes (e.g. Doubao's `<div></div>` breaks).
    blank(element: AnswerElement): boolean {
      for (const child of h.children(element)) {
        if (child.nodeType === 3) {
          if ((child.nodeValue ?? "").replace(INVISIBLE, "").trim()) return false;
        } else if (child.nodeType === 1) {
          const nested = child as AnswerElement;
          if (h.hidden(nested)) continue;
          if (h.tag(nested) === "hr" || !h.blank(nested)) return false;
        }
      }
      return true;
    },
    text(value: string, out: InlineToken[]): void {
      const text = value.replace(INVISIBLE, "");
      const entities = new Set<number>();
      for (const match of text.matchAll(ENTITY)) entities.add(match.index);
      let index = 0;
      for (const character of text) {
        if (SPACE.test(character)) out.push({ k: "sp" });
        else if (character === "&")
          out.push({ k: "c", s: entities.has(index) ? "\\&" : "&", c: "p" });
        else
          out.push({
            k: "c",
            s: ESCAPED.test(character) ? `\\${character}` : character,
            c: PUNCTUATION.test(character) ? "p" : "w",
          });
        index += character.length;
      }
    },
    inline(node: AnswerNode, context: InlineContext, out: InlineToken[]): void {
      for (const child of h.children(node)) h.inlineNode(child, context, out);
    },
    inlineNode(node: AnswerNode, context: InlineContext, out: InlineToken[]): void {
      if (node.nodeType === 3) {
        h.text(node.nodeValue ?? "", out);
        return;
      }
      if (node.nodeType !== 1) return;
      const element = node as AnswerElement;
      if (h.hidden(element)) return;
      const tag = h.tag(element);
      if (tag === "br" || tag === "hr") {
        out.push({ k: "br" });
      } else if (tag === "strong" || tag === "b") {
        h.wrap(element, context, out, "**");
      } else if (tag === "em" || tag === "i") {
        h.wrap(element, context, out, "*");
      } else if (tag === "code") {
        const span = h.codeSpan(element, context);
        if (span) out.push({ k: "a", s: span });
      } else if (tag === "a") {
        h.link(element, context, out);
      } else if (BLOCKS.has(tag)) {
        // A block nested in inline content renders on its own line(s).
        out.push({ k: "br" });
        h.inline(element, context, out);
        out.push({ k: "br" });
      } else {
        h.inline(element, context, out);
      }
    },
    wrap(
      element: AnswerElement,
      context: InlineContext,
      out: InlineToken[],
      mark: "**" | "*",
    ): void {
      if (mark === "**" ? context.strong : context.em) {
        h.inline(element, context, out);
        return;
      }
      out.push({ k: "d", open: true, mark });
      h.inline(
        element,
        mark === "**" ? { ...context, strong: true } : { ...context, em: true },
        out,
      );
      out.push({ k: "d", open: false, mark });
    },
    // Raw text for code: no escaping, `<br>` and block boundaries become newlines.
    plain(node: AnswerNode, out: string[]): void {
      for (const child of h.children(node)) {
        if (child.nodeType === 3) {
          out.push(child.nodeValue ?? "");
        } else if (child.nodeType === 1) {
          const element = child as AnswerElement;
          if (h.hidden(element)) continue;
          const tag = h.tag(element);
          if (tag === "br") {
            out.push("\n");
            continue;
          }
          h.plain(element, out);
          if (BLOCKS.has(tag) && !out[out.length - 1]?.endsWith("\n")) out.push("\n");
        }
      }
    },
    codeSpan(element: AnswerElement, context: InlineContext): string {
      const parts: string[] = [];
      h.plain(element, parts);
      let code = parts.join("").replace(INVISIBLE, "").replace(/\s+/gu, " ").trim();
      if (!code) return "";
      // GFM splits table rows before inline parsing, so a pipe in a cell needs `\|` even in code.
      if (context.cell) code = code.replace(/\|/gu, "\\|");
      let longest = 0;
      for (const run of code.matchAll(/`+/gu)) longest = Math.max(longest, run[0].length);
      const fence = "`".repeat(longest + 1);
      const pad = code.startsWith("`") || code.endsWith("`") ? " " : "";
      return `${fence}${pad}${code}${pad}${fence}`;
    },
    link(element: AnswerElement, context: InlineContext, out: InlineToken[]): void {
      let href = "";
      const raw = element.getAttribute("href");
      if (raw) {
        try {
          const url = new URL(raw, element.baseURI);
          if (url.protocol === "http:" || url.protocol === "https:") href = url.href;
        } catch {
          href = "";
        }
      }
      if (!href) {
        h.inline(element, context, out);
        return;
      }
      const inner: InlineToken[] = [];
      h.inline(element, context, inner);
      const label = h.finish(inner, "inline");
      if (!label) return;
      const destination = href.replace(
        /[\s()<>|\\`]/gu,
        (character) => `%${character.charCodeAt(0).toString(16).toUpperCase().padStart(2, "0")}`,
      );
      out.push({ k: "a", s: `[${label}](${destination})` });
    },
    kind(token: InlineToken | undefined): "w" | "p" | "s" {
      if (!token || token.k === "sp" || token.k === "br") return "s";
      if (token.k === "c") return token.c;
      return "p";
    },
    // One pass: whitespace/breaks move outside delimiters; empty pairs vanish; adjacent runs of
    // the same mark merge (`**a****b**` → `**ab**`).
    tidy(tokens: InlineToken[]): boolean {
      let changed = false;
      for (let index = 0; index + 1 < tokens.length; index++) {
        const left = tokens[index]!;
        const right = tokens[index + 1]!;
        if (left.k === "d" && left.open && (right.k === "sp" || right.k === "br")) {
          tokens[index] = right;
          tokens[index + 1] = left;
          changed = true;
        } else if (right.k === "d" && !right.open && (left.k === "sp" || left.k === "br")) {
          tokens[index] = right;
          tokens[index + 1] = left;
          changed = true;
        } else if (
          left.k === "d" &&
          right.k === "d" &&
          left.mark === right.mark &&
          left.open !== right.open
        ) {
          tokens.splice(index, 2);
          index = Math.max(-1, index - 2);
          changed = true;
        }
      }
      return changed;
    },
    // CommonMark flanking: a closing run after punctuation must be followed by whitespace or
    // punctuation, an opening run before punctuation must follow whitespace or punctuation.
    // Otherwise move one edge unit outside the run (at most one move per call).
    flank(tokens: InlineToken[]): boolean {
      for (let start = 0; start < tokens.length; start++) {
        if (tokens[start]!.k !== "d") continue;
        let end = start;
        while (tokens[end + 1]?.k === "d") end++;
        let opens = 0;
        let closes = 0;
        for (let index = start; index <= end; index++) {
          const token = tokens[index]!;
          if (token.k === "d" && token.open) opens++;
          else closes++;
        }
        const before = h.kind(tokens[start - 1]);
        const after = h.kind(tokens[end + 1]);
        if (!opens && before === "p" && after === "w") {
          const [unit] = tokens.splice(start - 1, 1);
          tokens.splice(end, 0, unit!);
          return true;
        }
        if (!closes && after === "p" && before === "w") {
          const [unit] = tokens.splice(end + 1, 1);
          tokens.splice(start, 0, unit!);
          return true;
        }
        start = end;
      }
      return false;
    },
    lineStart(line: string): string {
      if (/^[#>+<-]/u.test(line) || /^=+$/u.test(line)) return `\\${line}`;
      return line.replace(/^(\d{1,9})([.)])(?=\s|$)/u, "$1\\$2");
    },
    finish(input: InlineToken[], mode: FinishMode): string {
      const tokens = input.map(
        (token): InlineToken => (token.k === "br" && mode !== "block" ? { k: "sp" } : token),
      );
      for (let guard = tokens.length * 4 + 16; guard > 0; guard--) {
        if (!h.tidy(tokens) && !h.flank(tokens)) break;
      }
      const kept: InlineToken[] = [];
      for (const token of tokens) {
        const last = kept[kept.length - 1];
        if (token.k === "sp") {
          if (last && last.k !== "sp" && last.k !== "br") kept.push(token);
        } else if (token.k === "br") {
          while (kept[kept.length - 1]?.k === "sp") kept.pop();
          if (kept.length && kept[kept.length - 1]!.k !== "br") kept.push(token);
        } else {
          kept.push(token);
        }
      }
      while (kept.length && ["sp", "br"].includes(kept[kept.length - 1]!.k)) kept.pop();
      const lines: string[] = [];
      let line = "";
      for (const token of kept) {
        if (token.k === "br") {
          lines.push(line);
          line = "";
        } else if (token.k === "sp") line += " ";
        else if (token.k === "d") line += token.mark;
        else line += token.s;
      }
      lines.push(line);
      if (mode === "block") return lines.map((value) => h.lineStart(value)).join("  \n");
      const joined = lines.join(" ");
      // An ATX heading drops a trailing ` #…` run unless its first `#` is escaped.
      return mode === "heading" ? joined.replace(/(^|\s)(#+)$/u, "$1\\$2") : joined;
    },
    flush(buffer: InlineToken[], out: MarkdownBlock[]): void {
      const text = h.finish(buffer.splice(0), "block");
      if (text) out.push({ k: "p", s: text });
    },
    blocks(element: AnswerElement, context: InlineContext): MarkdownBlock[] {
      const out: MarkdownBlock[] = [];
      const buffer: InlineToken[] = [];
      for (const child of h.children(element)) {
        if (child.nodeType === 1) {
          const nested = child as AnswerElement;
          const tag = h.tag(nested);
          if (!h.hidden(nested) && BLOCKS.has(tag)) {
            if (tag !== "hr" && h.blank(nested)) {
              buffer.push({ k: "br" });
              continue;
            }
            h.flush(buffer, out);
            out.push(...h.block(nested, context));
            continue;
          }
        }
        h.inlineNode(child, context, buffer);
      }
      h.flush(buffer, out);
      return out;
    },
    block(element: AnswerElement, context: InlineContext): MarkdownBlock[] {
      const tag = h.tag(element);
      const level = /^h([1-6])$/u.exec(tag)?.[1];
      if (level) {
        const tokens: InlineToken[] = [];
        h.inline(element, context, tokens);
        const text = h.finish(tokens, "heading");
        return text ? [{ k: "h", s: `${"#".repeat(Number(level))} ${text}` }] : [];
      }
      if (tag === "ul" || tag === "ol") return h.list(element, tag === "ol", context);
      if (tag === "blockquote") {
        const inner = h.join(h.blocks(element, context), false);
        if (!inner) return [];
        const quoted = inner.split("\n").map((value) => (value ? `> ${value}` : ">"));
        return [{ k: "quote", s: quoted.join("\n") }];
      }
      if (tag === "pre") return h.pre(element);
      if (tag === "table") return h.table(element, context);
      if (tag === "hr") return [{ k: "hr", s: "---" }];
      return h.blocks(element, context);
    },
    number(value: string | null): number | undefined {
      const parsed = Number.parseInt(value ?? "", 10);
      return Number.isSafeInteger(parsed) && parsed >= 0 && parsed <= MAX_NUMBER
        ? parsed
        : undefined;
    },
    list(element: AnswerElement, ordered: boolean, context: InlineContext): MarkdownBlock[] {
      let next = (ordered ? h.number(element.getAttribute("start")) : undefined) ?? 1;
      const items: { n: number; blocks: MarkdownBlock[] }[] = [];
      for (const child of h.children(element)) {
        if (child.nodeType !== 1) continue;
        const nested = child as AnswerElement;
        if (h.hidden(nested)) continue;
        if (h.tag(nested) === "li") {
          const value = ordered ? h.number(nested.getAttribute("value")) : undefined;
          if (value !== undefined) next = value;
          items.push({ n: next, blocks: h.blocks(nested, context) });
          next += 1;
        } else {
          // Invalid but common: a nested list directly inside ul/ol belongs to the previous item.
          const blocks = h.block(nested, context);
          const last = items[items.length - 1];
          if (last) last.blocks.push(...blocks);
          else if (blocks.length) items.push({ n: next++, blocks });
        }
      }
      const rendered = items
        .map((item) => ({ n: item.n, s: h.join(item.blocks, true) }))
        .filter((item) => item.s);
      return rendered.length ? [{ k: "list", s: "", ordered, items: rendered }] : [];
    },
    renderList(block: MarkdownBlock, alternate: boolean): string {
      const lines: string[] = [];
      for (const item of block.items ?? []) {
        const marker = block.ordered
          ? `${item.n}${alternate ? ")" : "."} `
          : `${alternate ? "*" : "-"} `;
        const indent = " ".repeat(marker.length);
        item.s.split("\n").forEach((value, index) => {
          lines.push(index === 0 ? marker + value : value ? indent + value : "");
        });
      }
      return lines.join("\n");
    },
    // Blocks are separated by a blank line. Inside a list item a nested list follows its paragraph
    // directly (tight), unless it is an ordered list not starting at 1 (it could not interrupt the
    // paragraph). Adjacent lists of the same type switch marker so they stay separate lists.
    join(blocks: MarkdownBlock[], inItem: boolean): string {
      let out = "";
      let previous: MarkdownBlock | undefined;
      let alternate = false;
      for (const block of blocks) {
        let text = block.s;
        if (block.k === "list") {
          alternate =
            previous?.k === "list" && previous.ordered === block.ordered ? !alternate : false;
          text = h.renderList(block, alternate);
        }
        if (!text) continue;
        if (previous) {
          const tight =
            inItem &&
            previous.k === "p" &&
            block.k === "list" &&
            (!block.ordered || block.items?.[0]?.n === 1);
          out += tight ? "\n" : "\n\n";
        }
        out += text;
        previous = block;
      }
      return out;
    },
    pre(element: AnswerElement): MarkdownBlock[] {
      const parts: string[] = [];
      h.plain(element, parts);
      const code = parts.join("").replace(/\r\n?/gu, "\n").replace(/\n$/u, "");
      if (!code.trim()) return [];
      let fence = "```";
      for (const run of code.matchAll(/`{3,}/gu))
        if (run[0].length >= fence.length) fence = "`".repeat(run[0].length + 1);
      const codeChild = h
        .children(element)
        .find((child) => child.nodeType === 1 && h.tag(child as AnswerElement) === "code") as
        | AnswerElement
        | undefined;
      const classes = `${element.getAttribute("class") ?? ""} ${codeChild?.getAttribute("class") ?? ""}`;
      const language = /(?:^|\s)lang(?:uage)?-([\w#+.-]+)/u.exec(classes)?.[1] ?? "";
      return [{ k: "code", s: `${fence}${language}\n${code}\n${fence}` }];
    },
    row(row: AnswerElement, context: InlineContext): string[] {
      const cells: string[] = [];
      for (const child of h.children(row)) {
        if (child.nodeType !== 1) continue;
        const cell = child as AnswerElement;
        const tag = h.tag(cell);
        if (h.hidden(cell) || (tag !== "td" && tag !== "th")) continue;
        const tokens: InlineToken[] = [];
        h.inline(cell, context, tokens);
        cells.push(h.finish(tokens, "cell"));
      }
      return cells;
    },
    tableLine(cells: string[], width: number): string {
      return `| ${Array.from({ length: width }, (_, index) => cells[index] ?? "").join(" | ")} |`;
    },
    table(element: AnswerElement, context: InlineContext): MarkdownBlock[] {
      const out: MarkdownBlock[] = [];
      const rows: { cells: string[]; head: boolean }[] = [];
      const cellContext = { ...context, cell: true };
      for (const child of h.children(element)) {
        if (child.nodeType !== 1) continue;
        const section = child as AnswerElement;
        if (h.hidden(section)) continue;
        const tag = h.tag(section);
        if (tag === "caption") {
          const tokens: InlineToken[] = [];
          h.inline(section, context, tokens);
          h.flush(tokens, out);
        } else if (tag === "tr") {
          rows.push({ cells: h.row(section, cellContext), head: false });
        } else if (tag === "thead" || tag === "tbody" || tag === "tfoot") {
          for (const nested of h.children(section)) {
            const row = nested as AnswerElement;
            if (nested.nodeType === 1 && !h.hidden(row) && h.tag(row) === "tr")
              rows.push({ cells: h.row(row, cellContext), head: tag === "thead" });
          }
        }
      }
      const width = Math.max(0, ...rows.map((row) => row.cells.length));
      if (!width) return out;
      const headIndex = Math.max(
        0,
        rows.findIndex((row) => row.head),
      );
      const lines = [
        h.tableLine(rows[headIndex]!.cells, width),
        h.tableLine(new Array<string>(width).fill("---"), width),
        ...rows
          .filter((_, index) => index !== headIndex)
          .map((row) => h.tableLine(row.cells, width)),
      ];
      out.push({ k: "table", s: lines.join("\n") });
      return out;
    },
  };

  return h.join(h.block(root, { strong: false, em: false, cell: false }), false).trim();
}

/** Selectors used by the answer readers; a loaded Doubao page contract satisfies this shape. */
export interface AnswerSelectors {
  answerBody: string;
  answerIgnore?: string;
}

/**
 * Markdown of the visible `answerBody` elements inside `message`, joined by a blank line, with the
 * contract's `answerIgnore` applied. Returns undefined (never throws) when the conversion fails or
 * yields nothing, so callers fall back to {@link readAnswerPlainText}. Usable from a standalone
 * read-only script with the same page contract as the adapter.
 */
export async function readAnswerMarkdown(
  message: Locator,
  contract: { selectors: AnswerSelectors },
  timeoutMs?: number,
): Promise<string | undefined> {
  try {
    const parts: string[] = [];
    for (const body of await message.locator(contract.selectors.answerBody).all()) {
      if (!(await body.isVisible())) continue;
      const markdown = await body.evaluate(
        answerToMarkdown,
        contract.selectors.answerIgnore,
        timeoutMs === undefined ? undefined : { timeout: timeoutMs },
      );
      if (markdown) parts.push(markdown);
    }
    return parts.join("\n\n") || undefined;
  } catch {
    return undefined;
  }
}

/**
 * The plain answer exactly as the consumer-web adapter built it before Markdown (R8): visible
 * `answerBody` innerText joined by "\n", CRLF normalized, trimmed. Kept identical so a backfill
 * script can prove it is reading the same answer that was stored.
 */
export async function readAnswerPlainText(
  message: Locator,
  contract: { selectors: Pick<AnswerSelectors, "answerBody"> },
): Promise<string> {
  const chunks: string[] = [];
  for (const body of await message.locator(contract.selectors.answerBody).all())
    if (await body.isVisible()) chunks.push(await body.innerText());
  return chunks.join("\n").replace(/\r\n/g, "\n").trim();
}
