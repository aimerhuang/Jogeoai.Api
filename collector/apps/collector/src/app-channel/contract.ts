import { cardsSchema } from "./cards.ts";
import { constants } from "node:fs";
import { lstat, open } from "node:fs/promises";
import { join } from "node:path";
import { platformCodeSchema } from "@aivc/contracts";
import { z } from "zod";
import {
  appVersionSchema,
  localeSchema,
  packageNameSchema,
  type AppPlatformCode,
  type ResolvedBinding,
} from "./bindings.ts";
import { MAX_SWIPES_PER_CALL, selectorSchema, viewSelectorSchema } from "./driver.ts";
import { sourceListLabelSchema } from "./evidence.ts";
import { AppChannelError, setupIssues, type AppSetupIssue } from "./failure.ts";

// App screen contract (design §4): what an operator saw on the real App, one private file per
// platform at APP_COLLECTOR_CONTRACT_DIR/<platformCode>.json. The repository holds synthetic
// fixtures only. The flow acts on nothing that is not written here: a required view that is
// missing, or a dialog that is not declared, is a contract change, never a guess.

const timeoutMs = z.number().int().min(0).max(60_000);

// One scripted action.
//   { "tap": S }             wait for S (default 10 s) and tap it; a missing S is a contract change
//     "optional": true       ... unless optional: then S is looked for once (or for `timeoutMs`)
//                            and the step is skipped when it is not there
//     "unless": S2           skip the step when S2 is already visible
//   { "waitFor": S }         wait for S (default 10 s); a missing S is a contract change
//   { "swipe": "up", "count": n }   n plain swipes (finger direction), 1..10
//   { "back": true }         the system back key
export const stepSchema = z.union([
  z
    .object({
      tap: selectorSchema,
      timeoutMs: timeoutMs.optional(),
      optional: z.boolean().optional(),
      unless: selectorSchema.optional(),
    })
    .strict(),
  z.object({ waitFor: selectorSchema, timeoutMs: timeoutMs.optional() }).strict(),
  z
    .object({
      swipe: z.enum(["up", "down"]),
      count: z.number().int().min(1).max(MAX_SWIPES_PER_CALL),
    })
    .strict(),
  z.object({ back: z.literal(true) }).strict(),
]);
export type ContractStep = z.infer<typeof stepSchema>;
const steps = z.array(stepSchema).max(8);
const markers = z.array(selectorSchema).max(8);

// A control to tap, or the back key where the screen has none.
const closeSchema = z.union([selectorSchema, z.object({ back: z.literal(true) }).strict()]);

// What tells the bubble showing the sent text from the answer's own text views. It is compared
// with views of an exported hierarchy only, never looked up on the device, so it can also say
// what a selector cannot: `withoutDesc` holds for a view that has no content description. Some
// Apps give neither kind of view a resource ID and describe only the answer's text views (real
// device, Doubao 15.7.0: the answer view repeats its text as its description, the bubble has
// none).
const bubbleCondition = z.string().min(1).max(256);
export const questionBubbleSchema = z
  .object({
    id: bubbleCondition.optional(),
    className: bubbleCondition.optional(),
    withoutDesc: z.literal(true).optional(),
  })
  .strict()
  .refine(
    (bubble) =>
      bubble.id !== undefined || bubble.className !== undefined || bubble.withoutDesc === true,
    "needs at least one condition",
  );
export type QuestionBubble = z.infer<typeof questionBubbleSchema>;
// All given conditions must hold.
export function isQuestionBubble(
  node: { resourceId: string; className: string; desc: string },
  bubble: QuestionBubble,
): boolean {
  return (
    (bubble.id === undefined || node.resourceId === bubble.id) &&
    (bubble.className === undefined || node.className === bubble.className) &&
    (bubble.withoutDesc !== true || node.desc === "")
  );
}

const answerSchema = z.discriminatedUnion("method", [
  z
    .object({
      // Read the text views inside `container` from the exported hierarchy, in screen order.
      method: z.literal("hierarchy"),
      container: selectorSchema,
      // Views inside the container that are not answer text (source caption, disclaimer, time,
      // suggested follow-ups). The bubble showing the sent text is always left out.
      exclude: z.array(selectorSchema).max(20),
      // The bubble showing the sent text: a view is the bubble only if it meets these conditions
      // and its text is the sent text. Required, because the sent text is the bare question and
      // an answer may repeat it as a line of its own: by text alone that line would pass for the
      // bubble while the real one is scrolled off screen, and the answer would be saved without
      // everything above it.
      question: questionBubbleSchema,
      // The first view after the answer text, for example the first answer control. Texts from
      // it on are not answer text: suggested follow-ups that Apps show below the controls have
      // no selector of their own (real device, Doubao 15.7.0). Every view is asked in turn
      // whether it is this one, so the selector takes no `row`.
      end: viewSelectorSchema.optional(),
      // true: the answer may be longer than one screen, so scroll back to the sent question and
      // join the reads by their overlap. false: one read of the final screen is the whole answer.
      stitch: z.boolean(),
      // Upper bound for those scroll steps (default 20).
      maxSwipes: z.number().int().min(1).max(40).optional(),
    })
    .strict(),
  z
    .object({
      // Tap the App's own copy control and read the clipboard.
      method: z.literal("copy"),
      copy: selectorSchema,
    })
    .strict(),
]);

const countPattern = z
  .string()
  .min(1)
  .max(120)
  .refine((pattern) => {
    try {
      return new RegExp(`${pattern}|`, "u").exec("")!.length === 2;
    } catch {
      return false;
    }
  }, "needs a valid regular expression with exactly one capture group");

// A query parameter name, as `unwrap` and `dropParams` name them.
const paramNameSchema = z.string().regex(/^[A-Za-z0-9_.-]{1,64}$/u);
// A host name in lower case, as the URL parser writes it.
const hostSchema = z
  .string()
  .max(253)
  .regex(/^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/u);
// The link of every source, read one source at a time through what the App shows a user: the
// source's title is tapped, the page it opens is shared with "copy link", the clipboard is read
// through the driver and the page is left again (real device, Doubao 15.7.0: the panel lists
// titles only; a title opens the App's own page without an address bar, whose share sheet has
// "复制链接"). A source whose link is not got keeps its title, with `url` null and a fixed
// cause word; the list itself is read as before. Only with `itemTitle`: the title view is what
// is tapped.
export const linksSchema = z
  .object({
    // Visible while the page a source opened is on screen.
    page: selectorSchema,
    // The page's share control.
    share: selectorSchema,
    // "Copy link" in the share sheet.
    copy: selectorSchema,
    // Leaves the page and leads back to the source panel: a control, or the back key.
    close: closeSchema,
    // Upper bound for reading the links of one answer, in seconds (default 180). The sources
    // not opened by then keep their titles.
    budgetSeconds: z.number().int().min(30).max(600).optional(),
    // Restoration of the copied text (links.ts), offline: a host whose links carry the source's
    // address in a parameter, and parameters the App appends to an address.
    unwrap: z
      .array(z.object({ host: hostSchema, param: paramNameSchema }).strict())
      .max(5)
      .optional(),
    dropParams: z.array(paramNameSchema).max(20).optional(),
    // Android's own share sheet, which a page's share control can open instead of the App's
    // (real device, Doubao 15.7.0 on Android 15: `com.android.intentresolver`, its copy button
    // `com.android.intentresolver:id/copy`). Its package is tolerated in front from the tap on
    // `share` until the clipboard is read, and its copy button gives the link. Absent: such a
    // sheet is left with the back key and the source has no link (`share_external`).
    systemShare: z.object({ package: packageNameSchema, copy: selectorSchema }).strict().optional(),
  })
  .strict();
export type SourceLinks = z.infer<typeof linksSchema>;

// One way from the answer to a list of its sources: the entry, the screens it opens and how the
// list is read and left.
const sourcePathSchema = z
  .object({
    // Names the list where the contract declares several (see `citationsSchema`).
    label: sourceListLabelSchema.optional(),
    // Opens the App's source panel. Not visible means this answer shows no sources.
    entry: selectorSchema,
    // Swipes allowed to bring `entry` on screen when it is not visible after completion, one at
    // a time. An entry at the start of a long answer needs many; one that is not reached within
    // `max` is recorded as "this answer shows no sources". With `stopWhenUnmoved`, that many
    // swipes in a row that leave the exported hierarchy as it was end the search early: the
    // list is at its end. Leave it out for an App that holds a whole answer in one text view,
    // whose hierarchy stays the same while the answer scrolls (real device, Doubao 15.7.0).
    entrySwipe: z
      .object({
        swipe: z.enum(["up", "down"]),
        max: z.number().int().min(1).max(40),
        stopWhenUnmoved: z.number().int().min(2).max(10).optional(),
      })
      .strict()
      .optional(),
    // For an App whose `entry` does not open the source list but a screen in between (the tool
    // steps of the answer), from which rows lead to one source list each.
    via: z
      .object({
        // Visible while the screen in between is open.
        panel: selectorSchema,
        // The rows that lead to a source list; there may be none. They are taken from one
        // export of the screen as it opens (it is not scrolled), in screen order, each text
        // once, and each is then tapped by its exact text.
        item: selectorSchema,
        // A row whose text starts with this gives a search keyword: the rest of its text.
        queryPrefix: z.string().min(1).max(40).optional(),
        // Closes the screen in between.
        close: closeSchema,
        // How many rows are opened at most (default 5).
        maxItems: z.number().int().min(1).max(10).optional(),
      })
      .strict()
      .optional(),
    // Visible while the panel (the source list) is open.
    panel: selectorSchema,
    // How the sources are told apart. Exactly one of the two:
    // `itemTitle` — the title views have a selector of their own (Apps with resource IDs);
    // `cards` — every source is a clickable card whose texts follow a slot template (cards.ts).
    itemTitle: viewSelectorSchema.optional(),
    cards: cardsSchema.optional(),
    // Read only when the panel shows them next to a title; never derived from anything else.
    // With `cards` the site and URL come from the template's slots instead.
    itemSite: selectorSchema.optional(),
    itemUrl: selectorSchema.optional(),
    // The search-keyword line: the view matching `selector`, or the first text view that follows
    // the view matching `after` (and is neither a source title nor in `exclude`). `separator`
    // splits one line into keywords; without it the line is one entry.
    searchQueries: z
      .object({
        selector: selectorSchema.optional(),
        after: viewSelectorSchema.optional(),
        exclude: z.array(viewSelectorSchema).max(5).optional(),
        separator: z.string().min(1).max(8).optional(),
      })
      .strict()
      .refine(
        (value) => (value.selector === undefined) !== (value.after === undefined),
        "needs exactly one of selector and after",
      )
      .optional(),
    // Closes the panel: a control to tap, or the back key where the panel has none. With `via`
    // it leads back to the screen in between.
    close: closeSchema,
    // Upper bound for scrolling a long source list (default 10).
    maxSwipes: z.number().int().min(1).max(20).optional(),
    // The App states how many sources there are ("引用来源 24", "14 个网页"). When given, a list
    // read with a different number of sources is not saved: `pattern` must capture that number
    // in its first group, from the text of the tapped `entry` or of a view of the open panel
    // (the content description of a view that has no text).
    count: z
      .discriminatedUnion("from", [
        z.object({ from: z.literal("entry"), pattern: countPattern }).strict(),
        z
          .object({ from: z.literal("panel"), selector: selectorSchema, pattern: countPattern })
          .strict(),
      ])
      .optional(),
    // Opens every source to read its link (see `linksSchema`).
    links: linksSchema.optional(),
  })
  .strict()
  .refine(
    (value) => (value.itemTitle === undefined) !== (value.cards === undefined),
    "needs exactly one of itemTitle and cards",
  )
  // A card is one view whose texts are fitted to slots; which view is the one to tap, and how a
  // card is found again on the screen after its page was left, is not written yet. A list behind
  // a screen in between is left through that screen, which the way back from a page knows
  // nothing of.
  .refine((value) => value.links === undefined || value.cards === undefined, {
    path: ["links"],
    message: "cannot be combined with cards",
  })
  .refine((value) => value.links === undefined || value.via === undefined, {
    path: ["links"],
    message: "cannot be combined with via",
  })
  .refine(
    (value) =>
      value.cards === undefined || (value.itemSite === undefined && value.itemUrl === undefined),
    "itemSite and itemUrl belong to itemTitle; with cards use slots named site and url",
  )
  // Several lists are merged into one, and no screen states a number for that.
  .refine(
    (value) => value.via === undefined || value.count === undefined,
    "via and count cannot be combined",
  );
export type SourcePath = z.infer<typeof sourcePathSchema>;

// The sources of an answer: one path, or the 2 to 3 paths of an App whose answer shows several
// source lists behind different entries (design §16.7; real device, Kimi 3.1.8: the sources the
// answer cites at its end, and the pages its search steps returned at its start). Several paths
// are read one after the other in the order written, and each needs a `label` of its own, which
// the record repeats on every source of that list. A single path takes none: its record is
// what it was before there were several.
//
// Every path looks for its own `entry`. After a path that could not be read, or one with a
// screen in between (`via`), the end of the answer is first brought back on screen, and the
// next path is not begun without it; an entry that is not at the end of the answer therefore
// needs an `entrySwipe`, whichever path is read before it.
const singlePathSchema = sourcePathSchema.refine((path) => path.label === undefined, {
  path: ["label"],
  message: "belongs to a path of several",
});
const severalPathsSchema = z
  .array(
    sourcePathSchema.refine((path) => path.label !== undefined, {
      path: ["label"],
      message: "is required for a path of several",
    }),
  )
  .min(2)
  .max(3)
  .refine((paths) => {
    // A missing label is reported at its path; it is not also a label given twice.
    const labels = paths.flatMap((path) => (path.label === undefined ? [] : [path.label]));
    return new Set(labels).size === labels.length;
  }, "labels must be unique");
// The form alone decides which of the two a value is checked as, so that a mistake inside a
// path is reported at its own place instead of as "fits neither form". The contract holds what
// that check returned, like every other part of it, never the value it was given.
const citationsSchema = z.unknown().transform((value, context): SourcePath | SourcePath[] => {
  const checked = (Array.isArray(value) ? severalPathsSchema : singlePathSchema).safeParse(value);
  if (checked.success) return checked.data;
  for (const issue of checked.error.issues)
    context.addIssue({ code: "custom", path: [...issue.path], message: issue.message });
  return z.NEVER;
});
// The source paths of a contract in the order they are read; none where the App shows no sources.
export function sourcePaths(contract: Pick<AppContract, "citations">): SourcePath[] {
  const { citations } = contract;
  return citations === undefined ? [] : Array.isArray(citations) ? citations : [citations];
}

export const appContractSchema = z
  .object({
    version: z.literal(1),
    platformCode: platformCodeSchema,
    package: packageNameSchema,
    // Every App version whose screens were reviewed against this file.
    appVersions: z
      .array(appVersionSchema)
      .min(1)
      .max(10)
      .refine((versions) => new Set(versions).size === versions.length, "must be unique"),
    // System language the screens were reviewed in; text selectors depend on it.
    locale: localeSchema,
    // UTC time of the review: not in the future and at most seven days old.
    reviewedAt: z.iso.datetime(),
    // How to see that an account is logged in: `open` leads to the screen showing `marker`,
    // `close` leads back. Used by the self-check only.
    loggedIn: z.object({ open: steps, marker: selectorSchema, close: steps }).strict(),
    // Visible states that stop a binding. Visibility only: the script never touches these
    // screens. An empty list states that no such screen has been observed.
    blockers: z
      .object({
        login_required: markers,
        challenge_required: markers,
        rate_limited: markers,
        emulator_rejected: markers,
      })
      .strict(),
    // Unrelated dialogs the script may close (an upgrade or first-run guide). Anything else that
    // covers the screen is a contract change.
    popups: z.array(z.object({ marker: selectorSchema, dismiss: selectorSchema }).strict()).max(10),
    newConversation: z
      .object({
        steps: steps.min(1),
        // How an empty conversation is recognised after the steps; at least one of the two.
        // `emptyMarker` is a view only an empty conversation shows. Where an App has none (its
        // greeting changes from start to start), `emptyWhenAbsent` lists views that every
        // conversation with a message shows: the conversation is empty when none is visible.
        emptyMarker: selectorSchema.optional(),
        emptyWhenAbsent: z.array(selectorSchema).min(1).max(5).optional(),
      })
      .strict()
      .refine(
        (value) => value.emptyMarker !== undefined || value.emptyWhenAbsent !== undefined,
        "needs emptyMarker or emptyWhenAbsent",
      ),
    composer: z
      .object({
        // For example switching from voice to text input.
        prepare: steps,
        input: selectorSchema,
        send: selectorSchema,
      })
      .strict(),
    completion: z
      .object({
        // Run on every poll before looking for `marker`: brings the end of the answer on screen
        // (an optional tap on the App's jump-to-end control, or a bounded swipe). Only those two
        // step kinds are allowed here: a poll must never fail because the control is not shown.
        reveal: steps.refine(
          (list) => list.every((step) => "swipe" in step || ("tap" in step && step.optional)),
          "may only hold optional taps and swipes",
        ),
        // Present only once generation has ended.
        marker: selectorSchema,
        // While this is visible the answer is not complete, whatever else is.
        generating: selectorSchema.optional(),
        // Notices the App writes in place of an answer, looked for only while waiting for the
        // answer to the question just sent. Visibility only: nothing in them is tapped. They are
        // not `blockers` because an App may reopen on the last conversation, where yesterday's
        // notice still shows (real device, Kimi 3.1.8). `rate_limited`: the account gets no more
        // answers today; `declined`: the platform did not answer this time (a queue), the
        // account itself is fine. Absent: none observed.
        notices: z
          .array(
            z.object({ marker: selectorSchema, as: z.enum(["rate_limited", "declined"]) }).strict(),
          )
          .max(5)
          .optional(),
      })
      .strict(),
    answer: answerSchema,
    // Absent: the App shows no sources.
    citations: citationsSchema.optional(),
    // Looked up on the completed answer screen. Neither marker visible means "unknown".
    searchState: z
      .object({
        webSearchOn: selectorSchema.optional(),
        webSearchOff: selectorSchema.optional(),
        deepThinkingOn: selectorSchema.optional(),
        deepThinkingOff: selectorSchema.optional(),
      })
      .strict()
      .optional(),
  })
  .strict();
export type AppContract = z.infer<typeof appContractSchema>;

export type ContractRejectionReason =
  | "missing"
  | "unreadable"
  | "invalid"
  | "platform_mismatch"
  | "package_mismatch"
  | "version_mismatch"
  | "locale_mismatch"
  | "review_in_future"
  | "review_expired";
// Why a contract cannot be used. Every reason ends as `app_contract_unverified` for the binding;
// the reason and validator paths (never values) go to the log for the operator.
export class ContractRejection extends Error {
  constructor(
    readonly reason: ContractRejectionReason,
    readonly issues: AppSetupIssue[] = [],
  ) {
    super(reason);
    this.name = "ContractRejection";
  }
}

const MAX_CONTRACT_FILE_BYTES = 256 * 1024;
const REVIEW_MAX_AGE_MS = 7 * 86_400_000;
const REVIEW_FUTURE_TOLERANCE_MS = 60_000;

export function contractPath(directory: string, platformCode: AppPlatformCode): string {
  return join(directory, `${platformCode}.json`);
}

// Same private-file rule as the approved-question list (approval-guard.ts): a regular file, no
// symlink, owned by this user, no group/other bits, bounded size; O_NOFOLLOW + fstat closes the
// swap window between lstat and read.
export async function loadContractFile(
  directory: string,
  platformCode: AppPlatformCode,
): Promise<AppContract> {
  const path = contractPath(directory, platformCode);
  let text: string;
  try {
    const linkStat = await lstat(path);
    if (linkStat.isSymbolicLink() || !linkStat.isFile()) throw new ContractRejection("unreadable");
    const file = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW);
    try {
      const stat = await file.stat();
      if (
        !stat.isFile() ||
        stat.dev !== linkStat.dev ||
        stat.ino !== linkStat.ino ||
        stat.uid !== process.getuid?.() ||
        (stat.mode & 0o077) !== 0 ||
        stat.size > MAX_CONTRACT_FILE_BYTES
      )
        throw new ContractRejection("unreadable");
      text = await file.readFile("utf8");
    } finally {
      await file.close();
    }
  } catch (error) {
    if (error instanceof ContractRejection) throw error;
    throw new ContractRejection(
      (error as NodeJS.ErrnoException).code === "ENOENT" ? "missing" : "unreadable",
    );
  }
  let json: unknown;
  try {
    json = JSON.parse(text);
  } catch {
    throw new ContractRejection("invalid");
  }
  const parsed = appContractSchema.safeParse(json);
  if (!parsed.success) throw new ContractRejection("invalid", setupIssues(parsed.error));
  return parsed.data;
}

export interface ContractFacts {
  // Version installed on the device, already equal to the binding's reviewed version.
  installedAppVersion: string;
  deviceLocale: string;
  now: Date;
}
// The installed version, the binding's reviewed version and the contract's reviewed versions
// must agree, the contract's language must be the device's, and the review must be fresh.
export function contractRejection(
  contract: AppContract,
  binding: Pick<ResolvedBinding, "platformCode" | "package" | "reviewedAppVersion">,
  facts: ContractFacts,
): ContractRejectionReason | null {
  if (contract.platformCode !== binding.platformCode) return "platform_mismatch";
  if (contract.package !== binding.package) return "package_mismatch";
  if (
    facts.installedAppVersion !== binding.reviewedAppVersion ||
    !contract.appVersions.includes(facts.installedAppVersion)
  )
    return "version_mismatch";
  if (contract.locale !== facts.deviceLocale) return "locale_mismatch";
  const age = facts.now.getTime() - Date.parse(contract.reviewedAt);
  if (age < -REVIEW_FUTURE_TOLERANCE_MS) return "review_in_future";
  if (age > REVIEW_MAX_AGE_MS) return "review_expired";
  return null;
}

export interface ContractSourceOptions {
  load: (platformCode: AppPlatformCode) => Promise<AppContract>;
  log?: (line: Record<string, unknown>) => void;
}
// Gives the flow a contract it may act on, or fails the binding as `app_contract_unverified`.
// The file is read again for every attempt, so a re-review is picked up without a restart and a
// review that expires during a batch stops the binding.
export class ContractSource {
  constructor(private readonly options: ContractSourceOptions) {}

  static fromDirectory(
    directory: string,
    log?: (line: Record<string, unknown>) => void,
  ): ContractSource {
    return new ContractSource({
      load: (platformCode) => loadContractFile(directory, platformCode),
      log,
    });
  }

  async verified(
    binding: Pick<
      ResolvedBinding,
      "key" | "platformCode" | "package" | "reviewedAppVersion" | "device"
    >,
    facts: ContractFacts,
  ): Promise<AppContract> {
    let rejection: ContractRejection;
    try {
      const contract = await this.options.load(binding.platformCode);
      const reason = contractRejection(contract, binding, facts);
      if (reason === null) return contract;
      rejection = new ContractRejection(reason);
    } catch (error) {
      if (!(error instanceof ContractRejection)) throw error;
      rejection = error;
    }
    this.options.log?.({
      event: "app_contract_rejected",
      device: binding.device.id,
      platform: binding.platformCode,
      reason: rejection.reason,
      ...(rejection.issues.length > 0 ? { issues: rejection.issues } : {}),
    });
    throw new AppChannelError("app_contract_unverified", "pre_send");
  }
}
