import { describe, expect, it } from "vitest";
import { restoreLink } from "./links.ts";

// The App's own parameters and wrapper host as seen on the real device (Doubao 15.7.0); every
// address behind them is a placeholder.
const APP_PARAMS =
  "use_xbridge3=true&loader_name=forest&need_sec_link=1&sec_link_scene=im&theme=light";
const RULES = {
  unwrap: [{ host: "link.wtturl.cn", param: "target" }],
  dropParams: ["use_xbridge3", "loader_name", "need_sec_link", "sec_link_scene", "theme"],
};
const wrapped = (target: string) =>
  `https://link.wtturl.cn/?app_version=null&channel=null&seclink_version=1.1.0&scene=im&target=${encodeURIComponent(
    target,
  )}&sec_link_biz_tag=argus&seclink_settings_version=0a1b&jumper_version=1&device_platform=android&os_name=android&lang=cn&app_id=1&aid=1&did=null`;

describe("restoring a copied link offline", () => {
  it("takes the App's own parameters off the source's address", () => {
    expect(restoreLink(`https://example.com/news/1?${APP_PARAMS}`, RULES)).toEqual({
      url: "https://example.com/news/1",
      restored: true,
    });
  });

  it("takes the source's address out of the App's redirect, and its parameters off that", () => {
    expect(restoreLink(wrapped(`https://example.org/a/b.html?${APP_PARAMS}`), RULES)).toEqual({
      url: "https://example.org/a/b.html",
      restored: true,
    });
  });

  it("keeps the source's own parameters in their order and encoding", () => {
    const copied = `https://example.com/search?q=a%20b&page=2&x=%E4%B8%AD&${APP_PARAMS}`;
    expect(restoreLink(copied, RULES)).toEqual({
      url: "https://example.com/search?q=a%20b&page=2&x=%E4%B8%AD",
      restored: true,
    });
    // A parameter of the source that comes after the App's is kept as well.
    expect(restoreLink("https://example.com/p?theme=light&id=7&loader_name=forest", RULES)).toEqual(
      { url: "https://example.com/p?id=7", restored: true },
    );
  });

  it("keeps a fragment and writes a path outside ASCII in its encoded form", () => {
    expect(restoreLink(`https://example.com/p?${APP_PARAMS}#section-2`, RULES)).toEqual({
      url: "https://example.com/p#section-2",
      restored: true,
    });
    expect(
      restoreLink(wrapped(`https://example.org/合成/页面?${APP_PARAMS}#第二节`), RULES),
    ).toEqual({
      url: "https://example.org/%E5%90%88%E6%88%90/%E9%A1%B5%E9%9D%A2#%E7%AC%AC%E4%BA%8C%E8%8A%82",
      restored: true,
    });
  });

  it("gives back the copied text, not restored, when the redirect holds no web address", () => {
    const withoutTarget = "https://link.wtturl.cn/?scene=im&aid=1";
    expect(restoreLink(withoutTarget, RULES)).toEqual({ url: withoutTarget, restored: false });
    const notWeb = wrapped("javascript:alert(1)");
    expect(restoreLink(notWeb, RULES)).toEqual({ url: notWeb, restored: false });
    const relative = wrapped("/a/b");
    expect(restoreLink(relative, RULES)).toEqual({ url: relative, restored: false });
  });

  it("gives back text that is not a web address as it is, not restored", () => {
    for (const copied of ["合成文字", "ftp://example.com/file", "mailto:a@example.com", ""])
      expect(restoreLink(copied, RULES)).toEqual({ url: copied, restored: false });
  });

  it("undoes one layer of the redirect only", () => {
    const inner = wrapped(`https://example.org/x?${APP_PARAMS}`);
    const outer = wrapped(inner);
    const result = restoreLink(outer, RULES);
    expect(result.restored).toBe(true);
    expect(new URL(result.url).hostname).toBe("link.wtturl.cn");
    expect(new URL(result.url).searchParams.get("target")).toBe(
      `https://example.org/x?${APP_PARAMS}`,
    );
  });

  it("changes nothing a contract does not name", () => {
    expect(restoreLink(`https://example.com/p?${APP_PARAMS}`, {})).toEqual({
      url: `https://example.com/p?${APP_PARAMS}`,
      restored: true,
    });
    const link = wrapped("https://example.org/");
    expect(restoreLink(link, { dropParams: RULES.dropParams }).url).toBe(link);
  });

  it("handles a very long address without cutting it", () => {
    const path = "a".repeat(20_000);
    expect(restoreLink(`https://example.com/${path}?id=1&${APP_PARAMS}`, RULES)).toEqual({
      url: `https://example.com/${path}?id=1`,
      restored: true,
    });
    const long = `https://example.com/?${"x=1&".repeat(5_000)}theme=light`;
    expect(restoreLink(long, RULES).url).toBe(`https://example.com/?${"x=1&".repeat(4_999)}x=1`);
  });
});
