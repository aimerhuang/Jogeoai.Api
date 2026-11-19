import { z } from "zod";
import { descendants, type UiNode } from "./hierarchy.ts";

// Source lists of Apps that expose no resource IDs (Compose): every source is a clickable card
// holding a few text views in a fixed order, some of them optional. A contract describes that
// order as a slot template and the texts of each card are fitted to it. A card that does not fit
// is not a source that was read: it is the half-visible card at the edge of the list, and it is
// read on a later swipe once it is whole. Nothing is guessed from a card that does not fit.

const slotSchema = z
  .object({
    // "title", "site" and "url" go into the record; other names only describe the layout.
    name: z.string().regex(/^[a-z][A-Za-z]{0,19}$/u),
    // May be missing from a card.
    optional: z.boolean().optional(),
    // The whole text must match. A slot with a pattern never takes a text that does not match.
    pattern: z.string().min(1).max(120).optional(),
    // Takes every text between the slots before it and the slots after it: one or more, or
    // none where it is also optional (a snippet of any number of lines).
    many: z.literal(true).optional(),
  })
  .strict()
  .refine((slot) => {
    if (slot.pattern === undefined) return true;
    try {
      new RegExp(slot.pattern, "u");
      return true;
    } catch {
      return false;
    }
  }, "pattern is not a valid regular expression")
  .refine(
    (slot) => slot.many === undefined || slot.pattern === undefined,
    "a slot that takes many texts has no pattern",
  )
  // Several texts joined are not a title, a site or a link as the card shows it.
  .refine(
    (slot) => slot.many === undefined || !["title", "site", "url"].includes(slot.name),
    "a slot that takes many texts cannot be title, site or url",
  );
type Slot = z.infer<typeof slotSchema>;

export const cardsSchema = z
  .object({
    // Where a card's texts are. "text" (default): the text views inside the card, in screen
    // order. "desc": the lines of the card's own content description, for an App that draws its
    // own screen and describes a whole card in one view (real device, Zhipu Qingyan 3.8.7).
    from: z.enum(["text", "desc"]).optional(),
    layout: z.array(slotSchema).min(1).max(8),
    // true: every card of this list is equally tall. A card that is clearly less tall than the
    // usual card of the same export (`wholeCards`) is then the half card at an edge of the list
    // and is not read. Needed where the template has an optional slot: a half card showing only
    // its title fits such a template as a source without a site, and the same source is read
    // again, whole, after the next swipe.
    sameHeight: z.boolean().optional(),
  })
  .strict()
  .refine(
    (cards) => cards.layout.filter((slot) => slot.name === "title" && !slot.optional).length === 1,
    "layout needs exactly one required slot named title",
  )
  .refine(
    (cards) => new Set(cards.layout.map((slot) => slot.name)).size === cards.layout.length,
    "slot names must be unique",
  )
  .refine(
    (cards) => cards.layout.filter((slot) => slot.many).length <= 1,
    "layout takes at most one slot of many texts",
  )
  // Such a slot would take whatever text comes next, including one meant for a later slot.
  // With a slot of many texts the slots after it are fitted from the end of the card, so there
  // no optional slot can do without a pattern.
  .refine((cards) => {
    const many = cards.layout.some((slot) => slot.many);
    return cards.layout.every(
      (slot, index) =>
        slot.many ||
        !slot.optional ||
        slot.pattern !== undefined ||
        (!many && index === cards.layout.length - 1),
    );
  }, "an optional slot without a pattern must be the last slot, and cannot share a layout with a slot of many texts");
export type CardLayout = z.infer<typeof cardsSchema>;

const slotTakes = (slot: Slot, text: string | undefined): text is string =>
  text !== undefined &&
  (slot.pattern === undefined || new RegExp(`^(?:${slot.pattern})$`, "u").test(text));
// Fits texts to slots one after the other, from the first of both. Returns how many texts were
// taken, or undefined when a required slot got none.
function fitRun(
  texts: readonly string[],
  slots: readonly Slot[],
  fields: Record<string, string>,
): number | undefined {
  let next = 0;
  for (const slot of slots) {
    const text = texts[next];
    if (slotTakes(slot, text)) {
      fields[slot.name] = text;
      next += 1;
    } else if (!slot.optional) return undefined;
  }
  return next;
}
// Fits a card's texts, in screen order, to the template. Every text must land in a slot and
// every required slot must get one; otherwise the card does not fit.
//
// With a slot of many texts, the slots before it are fitted from the start of the card and the
// slots after it from its end; the texts left between them are that slot's, joined by line
// breaks.
export function fitCard(
  texts: readonly string[],
  cards: CardLayout,
): Record<string, string> | undefined {
  const fields: Record<string, string> = {};
  const at = cards.layout.findIndex((slot) => slot.many);
  if (at < 0) return fitRun(texts, cards.layout, fields) === texts.length ? fields : undefined;
  const many = cards.layout[at]!;
  const head = fitRun(texts, cards.layout.slice(0, at), fields);
  if (head === undefined) return undefined;
  const tail = fitRun(texts.slice(head).reverse(), cards.layout.slice(at + 1).reverse(), fields);
  if (tail === undefined) return undefined;
  const between = texts.slice(head, texts.length - tail);
  if (between.length > 0) fields[many.name] = between.join("\n");
  else if (!many.optional) return undefined;
  return fields;
}

type CardSource = NonNullable<CardLayout["from"]>;
// The cards of one hierarchy export: clickable views that hold text and no other such view.
// With "desc" the text a view holds is its own content description.
export function cardNodes(root: UiNode, from: CardSource = "text"): UiNode[] {
  const holdsText = (node: UiNode) =>
    from === "desc"
      ? node.desc.trim() !== ""
      : [...descendants(node)].some((item) => item.text.trim() !== "");
  const candidates = [...descendants(root)].filter((node) => node.clickable && holdsText(node));
  return candidates.filter(
    (node) => ![...descendants(node)].some((inner) => candidates.includes(inner)),
  );
}
// Of the cards of one export, those the list's edge does not cut: at least nine tenths as tall
// as the usual card. The usual height, not the tallest: on a real screen "equal height" is equal
// within a pixel of rounding, and the first and the last card of a list carry the list's padding
// (real device, Kimi 3.1.8: 105 px cards, a 104 px one among them, 126 px at both ends of the
// list).
//
// The usual height is the one most cards have (the taller one on a tie), counted among the cards
// that have a card on either side of them: only the first and the last card of an export can be
// cut by an edge or carry the padding, so those two say nothing about the rest. Counted among
// all cards, the two padded ends of a short list that is on screen whole would outnumber or
// equal the cards between them (126, 105, 126 or 126, 105, 105, 126) and those would be taken
// for cut cards and never be read. An export of one or two cards has no card in between; there
// every card is counted.
//
// A card cut by less than a tenth still shows every text view it holds; one cut by more is read
// after the next swipe. A card without bounds counts as having no height.
export function wholeCards(cards: readonly UiNode[]): UiNode[] {
  const height = (card: UiNode) => (card.bounds ? card.bounds.bottom - card.bounds.top : 0);
  const counts = new Map<number, number>();
  for (const card of cards.length > 2 ? cards.slice(1, -1) : cards)
    counts.set(height(card), (counts.get(height(card)) ?? 0) + 1);
  let usual = 0;
  let most = 0;
  for (const [value, count] of counts)
    if (count > most || (count === most && value > usual)) [usual, most] = [value, count];
  return cards.filter((card) => height(card) * 10 >= usual * 9);
}
// A card's texts in screen order: its text views, or the lines of its content description.
export function cardTexts(card: UiNode, from: CardSource = "text"): string[] {
  return (from === "desc" ? card.desc.split("\n") : [...descendants(card)].map((node) => node.text))
    .map((text) => text.trim())
    .filter((text) => text !== "");
}
