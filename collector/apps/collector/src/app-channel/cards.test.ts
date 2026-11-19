import { describe, expect, it } from "vitest";
import { cardNodes, cardsSchema, cardTexts, fitCard, wholeCards } from "./cards.ts";
import { parseHierarchy } from "./hierarchy.ts";

// Shape of a real ID-less source list: site, an optional date, a running number, the title and
// an optional snippet.
const layout = cardsSchema.parse({
  layout: [
    { name: "site" },
    { name: "date", optional: true, pattern: "\\d{4}/\\d{2}/\\d{2}" },
    { name: "index", pattern: "\\d{1,3}" },
    { name: "title" },
    { name: "snippet", optional: true },
  ],
});

describe("source card template", () => {
  it("fits a whole card, with and without its optional texts", () => {
    expect(fitCard(["合成站点", "2026/01/20", "1", "合成标题", "合成摘要"], layout)).toEqual({
      site: "合成站点",
      date: "2026/01/20",
      index: "1",
      title: "合成标题",
      snippet: "合成摘要",
    });
    expect(fitCard(["合成站点", "5", "合成标题", "合成摘要"], layout)).toMatchObject({
      site: "合成站点",
      title: "合成标题",
    });
    expect(fitCard(["合成站点", "5", "合成标题"], layout)).toEqual({
      site: "合成站点",
      index: "5",
      title: "合成标题",
    });
  });

  it("does not fit a card cut off at the top or the bottom of the list", () => {
    // Top: the site line has scrolled away.
    expect(fitCard(["3", "合成标题", "合成摘要"], layout)).toBeUndefined();
    expect(fitCard(["合成标题", "合成摘要"], layout)).toBeUndefined();
    // Bottom: the title is not on screen yet.
    expect(fitCard(["合成站点", "2026/01/20", "4"], layout)).toBeUndefined();
    expect(fitCard(["合成站点", "4"], layout)).toBeUndefined();
    expect(fitCard(["合成站点"], layout)).toBeUndefined();
    expect(fitCard([], layout)).toBeUndefined();
  });

  it("does not fit a card with a text the template does not know", () => {
    expect(fitCard(["合成站点", "1", "合成标题", "合成摘要", "合成角标"], layout)).toBeUndefined();
  });

  it("matches a pattern against the whole text only", () => {
    // "12 款" is not a running number, so it cannot be the index.
    expect(fitCard(["合成站点", "12 款", "合成标题"], layout)).toBeUndefined();
  });

  it("refuses templates that cannot be fitted unambiguously", () => {
    const refuse = (value: unknown) => expect(cardsSchema.safeParse(value).success).toBe(false);
    refuse({ layout: [{ name: "site" }] });
    refuse({ layout: [{ name: "title", optional: true }] });
    refuse({ layout: [{ name: "title" }, { name: "title" }] });
    refuse({ layout: [{ name: "title" }, { name: "index", pattern: "(" }] });
    // An optional slot without a pattern would swallow the title.
    refuse({ layout: [{ name: "site", optional: true }, { name: "title" }] });
    refuse({ layout: [{ name: "title" }, { name: "extra" }], other: true });
  });

  it("keeps, of equally tall cards, those about as tall as the usual card of the export", () => {
    const card = (top: number, bottom: number, texts: string[]) =>
      `<android.view.View class="android.view.View" clickable="true" bounds="[0,${top}][1080,${bottom}]">${texts
        .map(
          (value) =>
            `<android.widget.TextView class="android.widget.TextView" text="${value}" clickable="false" bounds="[0,${top}][540,${bottom}]" />`,
        )
        .join("")}</android.view.View>`;
    const cards = cardNodes(
      parseHierarchy(
        `<?xml version="1.0"?><hierarchy>${[
          // Cut by the top edge of the list: only its title shows.
          card(200, 260, ["合成标题一"]),
          card(260, 420, ["合成标题二", "合成站点二"]),
          // A whole card that shows no site.
          card(420, 580, ["合成标题三"]),
          // Cut by the bottom edge, with both texts still in the hierarchy.
          card(580, 700, ["合成标题四", "合成站点四"]),
        ].join("")}</hierarchy>`,
      ),
    );
    expect(cards).toHaveLength(4);
    expect(wholeCards(cards).map((card) => cardTexts(card))).toEqual([
      ["合成标题二", "合成站点二"],
      ["合成标题三"],
    ]);
    expect(wholeCards([])).toEqual([]);
    // A single card is its own measure.
    expect(wholeCards(cards.slice(0, 1))).toHaveLength(1);
    // Equal height is equal within rounding: a card one pixel shorter is still whole.
    const rounded = cardNodes(
      parseHierarchy(
        `<?xml version="1.0"?><hierarchy>${[
          card(301, 406, ["合成标题五", "合成站点五"]),
          card(406, 510, ["合成标题六"]),
          card(510, 615, ["合成标题七", "合成站点七"]),
        ].join("")}</hierarchy>`,
      ),
    );
    expect(wholeCards(rounded)).toHaveLength(3);
    // The first and the last card of a list are taller by the list's padding. The usual height
    // is the measure, not the tallest: otherwise only those two would count as whole.
    const padded = cardNodes(
      parseHierarchy(
        `<?xml version="1.0"?><hierarchy>${[
          card(280, 406, ["合成标题八", "合成站点八"]),
          card(406, 511, ["合成标题九", "合成站点九"]),
          card(511, 616, ["合成标题十"]),
          card(616, 721, ["合成标题十一", "合成站点十一"]),
          // Cut by the bottom edge: only the top of its title shows.
          card(721, 755, ["合成标题十二"]),
        ].join("")}</hierarchy>`,
      ),
    );
    expect(wholeCards(padded).map((item) => cardTexts(item)[0])).toEqual([
      "合成标题八",
      "合成标题九",
      "合成标题十",
      "合成标题十一",
    ]);

    // A short list is on screen whole, both padded ends included: the two ends then outnumber or
    // equal the cards between them, and are not the measure for those.
    const short = (heights: number[]) => {
      let top = 300;
      return cardNodes(
        parseHierarchy(
          `<?xml version="1.0"?><hierarchy>${heights
            .map((height, index) => card(top, (top += height), [`合成标题${index + 1}`]))
            .join("")}</hierarchy>`,
        ),
      );
    };
    for (const heights of [
      [126, 105, 126],
      [126, 105, 105, 126],
      // Rounding splits the cards between the ends into two heights.
      [126, 105, 104, 105, 104, 126],
      [126, 126],
      [147],
    ])
      expect(wholeCards(short(heights))).toHaveLength(heights.length);
    // The end of a list after a swipe: a cut card, one whole card, the padded last card.
    expect(wholeCards(short([40, 105, 126])).map((item) => cardTexts(item)[0])).toEqual([
      "合成标题2",
      "合成标题3",
    ]);
    // Two cards, one of them cut: the taller one is the measure.
    expect(wholeCards(short([60, 105])).map((item) => cardTexts(item)[0])).toEqual(["合成标题2"]);
    expect(wholeCards(short([105, 60])).map((item) => cardTexts(item)[0])).toEqual(["合成标题1"]);

    const template = { layout: [{ name: "title" }, { name: "site", optional: true }] };
    expect(cardsSchema.safeParse({ ...template, sameHeight: true }).success).toBe(true);
    expect(cardsSchema.safeParse({ ...template, sameHeight: false }).success).toBe(true);
    expect(cardsSchema.safeParse({ ...template, sameHeight: "yes" }).success).toBe(false);
    expect(cardsSchema.safeParse({ ...template, sameWidth: true }).success).toBe(false);
  });

  // A card an App describes in one view: title, a snippet of any number of lines, a badge, the
  // link (design §16.8).
  const described = cardsSchema.parse({
    from: "desc",
    layout: [
      { name: "title" },
      { name: "snippet", many: true, optional: true },
      { name: "url", pattern: "https?://\\S+" },
    ],
  });
  it("gives the texts between the first and the last slots to the slot of many texts", () => {
    expect(
      fitCard(["合成标题", "摘要一", "摘要二", "知", "https://example.invalid/a"], described),
    ).toEqual({
      title: "合成标题",
      snippet: "摘要一\n摘要二\n知",
      url: "https://example.invalid/a",
    });
    // No text between them: the slot is optional here.
    expect(fitCard(["合成标题", "https://example.invalid/a"], described)).toEqual({
      title: "合成标题",
      url: "https://example.invalid/a",
    });
    // The link is required: a card without one is not a source that was read.
    expect(fitCard(["合成标题", "摘要一", "知"], described)).toBeUndefined();
    expect(fitCard(["纱罩"], described)).toBeUndefined();
    expect(fitCard(["https://example.invalid/a"], described)).toBeUndefined();
    expect(fitCard([], described)).toBeUndefined();
  });
  it("fits the slots after a slot of many texts from the end of the card", () => {
    const tailed = cardsSchema.parse({
      layout: [
        { name: "index", pattern: "\\d+" },
        { name: "title" },
        { name: "body", many: true },
        { name: "date", optional: true, pattern: "\\d{4}/\\d{2}/\\d{2}" },
        { name: "url", pattern: "https?://\\S+" },
      ],
    });
    expect(
      fitCard(["1", "合成标题", "正文", "2026/01/20", "https://example.invalid/a"], tailed),
    ).toEqual({
      index: "1",
      title: "合成标题",
      body: "正文",
      date: "2026/01/20",
      url: "https://example.invalid/a",
    });
    expect(
      fitCard(["1", "合成标题", "正文一", "正文二", "https://example.invalid/a"], tailed),
    ).toEqual({
      index: "1",
      title: "合成标题",
      body: "正文一\n正文二",
      url: "https://example.invalid/a",
    });
    // Required here: with nothing left for it the card does not fit.
    expect(fitCard(["1", "合成标题", "https://example.invalid/a"], tailed)).toBeUndefined();
    expect(
      fitCard(["1", "合成标题", "2026/01/20", "https://example.invalid/a"], tailed),
    ).toBeUndefined();
  });
  it("refuses a slot of many texts that could not be fitted unambiguously", () => {
    const refuse = (layout: unknown[]) =>
      expect(cardsSchema.safeParse({ layout }).success).toBe(false);
    const title = { name: "title" };
    refuse([title, { name: "a", many: true }, { name: "b", many: true }]);
    refuse([title, { name: "snippet", many: true, pattern: ".+" }]);
    refuse([{ name: "title", many: true }]);
    refuse([title, { name: "site", many: true }]);
    refuse([title, { name: "url", many: true }]);
    refuse([title, { name: "snippet", many: false }]);
    // An optional slot without a pattern, before or after it, would take one of its texts.
    refuse([title, { name: "snippet", many: true }, { name: "note", optional: true }]);
    refuse([{ name: "note", optional: true }, title, { name: "snippet", many: true }]);
    expect(cardsSchema.safeParse({ from: "desc", layout: [title] }).success).toBe(true);
    expect(cardsSchema.safeParse({ from: "text", layout: [title] }).success).toBe(true);
    expect(cardsSchema.safeParse({ from: "hint", layout: [title] }).success).toBe(false);
  });
  it("takes clickable views with a description as the cards and its lines as their texts", () => {
    const view = (attributes: string, inner = "") =>
      `<android.view.View class="android.view.View" text="" ${attributes} bounds="[0,0][10,10]">${inner}</android.view.View>`;
    const xml = `<?xml version="1.0"?><hierarchy>${view(
      'clickable="true" content-desc="纱罩"',
    )}${view('clickable="false" content-desc="参考资料"')}${view(
      'clickable="true" content-desc="合成标题一&#10;  摘要一  &#10;&#10;知&#10;https://example.invalid/1"',
    )}${view(
      // A clickable wrapper that describes itself is not a card while it holds one.
      'clickable="true" content-desc="合成分组"',
      view('clickable="true" content-desc="合成标题二&#10;http://example.invalid/2"'),
    )}${view('clickable="true" content-desc=""')}</hierarchy>`;
    const root = parseHierarchy(xml);
    const cards = cardNodes(root, "desc");
    expect(cards.map((card) => cardTexts(card, "desc"))).toEqual([
      ["纱罩"],
      ["合成标题一", "摘要一", "知", "https://example.invalid/1"],
      ["合成标题二", "http://example.invalid/2"],
    ]);
    expect(
      cards.map((card) => fitCard(cardTexts(card, "desc"), described)).filter(Boolean),
    ).toEqual([
      { title: "合成标题一", snippet: "摘要一\n知", url: "https://example.invalid/1" },
      { title: "合成标题二", url: "http://example.invalid/2" },
    ]);
    // Read by text, as before, these views hold nothing.
    expect(cardNodes(root)).toEqual([]);
  });

  it("takes the innermost clickable views that hold text as the cards", () => {
    const view = (attributes: string, inner = "") =>
      `<android.view.View ${attributes} bounds="[0,0][10,10]">${inner}</android.view.View>`;
    const text = (value: string) =>
      `<android.widget.TextView class="android.widget.TextView" text="${value}" clickable="false" bounds="[0,0][10,10]" />`;
    const xml = `<?xml version="1.0"?><hierarchy>${view(
      'class="android.view.View" clickable="true"',
      // A clickable wrapper around two cards is not itself a card.
      view('class="android.view.View" clickable="true"', text("合成站点一") + text("合成标题一")) +
        view('class="android.view.View" clickable="true"', text("合成站点二") + text("合成标题二")),
    )}${view('class="android.view.View" clickable="true" content-desc="关闭"')}${text("合成面板标题")}</hierarchy>`;
    const cards = cardNodes(parseHierarchy(xml));
    expect(cards.map((card) => cardTexts(card))).toEqual([
      ["合成站点一", "合成标题一"],
      ["合成站点二", "合成标题二"],
    ]);
  });
});
