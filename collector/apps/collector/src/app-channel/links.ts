// Offline restoration of a link an App's "copy link" put on the clipboard (design of task
// 11-24, §6). An App can hand out the address of a source wrapped in a redirect of its own, or
// with parameters of its own appended to the source's address (real device, Doubao 15.7.0:
// `https://link.wtturl.cn/?…&target=<encoded address>&…`, and `use_xbridge3`, `loader_name`,
// `need_sec_link`, `sec_link_scene`, `theme` after the source's own parameters). Both can be
// undone from the copied text alone: nothing here goes on the network, follows a redirect or
// completes anything the text does not hold. What is undone is the contract's to say.

export interface LinkRules {
  // A host whose links carry the source's address in one query parameter.
  unwrap?: readonly { host: string; param: string }[];
  // Query parameters the App appends; every one of these names is taken out, the source's own
  // parameters are kept in their order and as they were written.
  dropParams?: readonly string[];
}
export interface RestoredLink {
  // The source's address; where it could not be restored, the copied text as it is.
  url: string;
  restored: boolean;
}

// A web address the URL parser accepts, or nothing.
function webUrl(text: string): URL | undefined {
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    return undefined;
  }
  return url.protocol === "http:" || url.protocol === "https:" ? url : undefined;
}
// The name of one `name=value` part of a query, decoded as a form value is. A name that does not
// decode is compared as it is written.
function paramName(part: string): string {
  const name = part.split("=", 1)[0]!.replaceAll("+", " ");
  try {
    return decodeURIComponent(name);
  } catch {
    return name;
  }
}

export function restoreLink(copied: string, rules: LinkRules): RestoredLink {
  const failed = { url: copied, restored: false };
  let url = webUrl(copied);
  if (!url) return failed;
  // One layer only: an address inside the wrapper that is itself wrapped stays as it is.
  const wrapper = rules.unwrap?.find((rule) => rule.host === url!.hostname);
  if (wrapper) {
    // `get` decodes the value once.
    const target = url.searchParams.get(wrapper.param);
    url = target === null ? undefined : webUrl(target);
    if (!url) return failed;
  }
  // The query is cut into its parts as written, not rebuilt from decoded values: a parameter of
  // the source keeps its own encoding.
  const drop = new Set(rules.dropParams ?? []);
  const kept = url.search
    .slice(1)
    .split("&")
    .filter((part) => part !== "" && !drop.has(paramName(part)));
  url.search = kept.length > 0 ? `?${kept.join("&")}` : "";
  return { url: url.href, restored: true };
}
