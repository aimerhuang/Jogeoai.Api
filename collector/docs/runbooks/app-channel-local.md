# App channel on local emulators (attended, explicit opt-in)

> The control-plane mode (`collector:app:serve`), which leases tasks from Jogeoai and runs
> unattended, has its own runbook: [app-channel-node.md](app-channel-node.md). This runbook covers
> the local trial (`check`, `run`, `report`, `prune`); devices, contracts and failure classes are
> the same in both.

## Status and boundary

A local script asks operator-approved questions in the Android Apps of the domestic AI
platforms on two emulators and keeps the answer, the visible sources, a screenshot and the UI
hierarchy of every attempt, and a screen recording when recording is switched on. It is a local
trial slice of the parent PRD's FR-14: nothing is sent to the control plane, and no sample
enters formal metrics.

Since v1.1 (2026-11-16) the goal is to get "ask, answer, collect" working first:

- the text sent is the approved question itself, with nothing before it (the Web channel
  dropped its prefix on the same day);
- a sample is valid when the complete answer was collected; sources are read as far as the App
  allows, and a source read that fails does not fail the sample (see "Sources are best effort");
- screen recording is off by default (`APP_COLLECTOR_RECORD_SCREEN`).

Platform status as of 2026-11-16 (details and evidence:
`.trellis/tasks/11-15-local-emulator-app-collection/research/driver-spike.md`, sections 18 and 19).
"Collected" means attended v1.1 runs on real devices with the bare question and recording
off: batch `smoke-v11-20261116` (one question per binding) and batch `full-v11-20261116` (the
four built-in questions on every platform), together 24 questions sent and 24 valid samples,
six per platform. The runs lasted minutes; nothing is known yet about a day-long success rate
or about a run with the project's own approved question list.

| Platform        | Status               | Note                                                                                                                                                                                                  |
| --------------- | -------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Doubao          | collected (v1.1)     | answer read from the hierarchy, sources by title; the App appends its own promotion text                                                                                                              |
| DeepSeek        | collected (v1.1)     | answer by the App's copy control, sources by site and title                                                                                                                                           |
| Tencent Yuanbao | collected (v1.1)     | answer by copy, sources by site and title                                                                                                                                                             |
| Qwen            | collected (v1.1)     | answer by copy; contract without a source section (the panel stays blank on the emulator)                                                                                                             |
| Kimi            | collected (v1.1)     | answer by copy (Markdown text); two source lists: `cited` (the sources entry at the end of the answer) and `search` (web-search results behind the tool steps at its start, with the search keywords) |
| Zhipu Qingyan   | collected (v1.1)     | answer by copy (plain text; a chart is copied as its source text); sources by title and link, no site name                                                                                            |
| ERNIE           | disabled, not usable | chat content is not exposed by the App's WebView                                                                                                                                                      |

The bindings of ERNIE are disabled in `apps/collector/app-bindings.json` with this finding
as their reason. Enable one again only together with a reviewed contract.
Zhipu Qingyan was enabled on 2026-11-16 after its smoke (batch `smoke-qingyan-20261116`: one
question per device, two valid samples; a first attempt was counted as sent without asking
anything, see "Reviewing a contract" on its composer). It is not part of the 24 samples above.
Kimi was enabled on 2026-11-16 after its smoke (batch `smoke-kimi-v11-20261116`). Its home
screen plays an animation that keeps the emulator's CPU busy, so run it while the host is
otherwise idle, and keep recording off for it: with recording on it stopped responding at start.

Never, in any configuration:

- hide emulator properties or change device identifiers;
- solve, tap through or automate a login, CAPTCHA or device verification;
- randomize input timing to look human;
- record the screen through the emulator console (`adb emu screenrecord`): it crashed the
  emulator process on 2026-11-16.

An App that refuses the emulator or asks for device verification is marked unavailable and left
alone.

## Prerequisites

- Node 24.22.1 or later within 24.x (WebdriverIO's declared minimum; `.node-version` pins
  24.22.3) and pnpm 10.34.5. The Appium binary runs through the `node` found on `PATH`, so
  `node -v` must report 24.
- Android SDK with `adb` on `PATH`, `ANDROID_HOME` or `ANDROID_SDK_ROOT` set, and a JDK
  (`JAVA_HOME`).
- `ffprobe`, only when screen recording is switched on.
- Both emulators running and every App logged in by hand.
- `pnpm install --frozen-lockfile` done in this repository.

The automation driver installs three helper packages on each device
(`io.appium.settings`, `io.appium.uiautomator2.server`, `io.appium.uiautomator2.server.test`).
At every session start it also relaxes the device's hidden-API policy and restores it when the
session ends. Remove the helpers with:

```bash
adb -s <serial> uninstall io.appium.settings
```

and the same for the other two packages.

## Devices

Devices are identified by AVD name, not by serial. The declared properties are in
`apps/collector/app-bindings.json`; a device whose real properties differ is not used.

Start an emulator (same arguments for both, name and port differ):

```bash
emulator -avd AI_Apps_4KB -port 5556 -no-snapshot -no-boot-anim -memory 4096 -no-metrics -crash-report-mode never
```

Every emulator start resets the time zone to the host's. Set it again after each start; the
script only checks it:

```bash
adb -s emulator-5556 shell cmd alarm set-timezone Asia/Shanghai
```

The system language must be `zh-Hans-CN`. An App with its own language setting (Kimi) must be
switched to Chinese inside the App.

Run batches while the host is otherwise idle. Under host load the Apps start slowly and Android
shows "isn't responding"; the script answers that dialog with "wait" at most five times during a
start and then counts the App as crashed.

## Private directories

Both must be absolute, outside every Git checkout, and contain an `aivc/` path component.

| Variable                     | Content                                                              |
| ---------------------------- | -------------------------------------------------------------------- |
| `APP_COLLECTOR_EVIDENCE_DIR` | batches, records, screenshots, videos if recorded, the send ledger   |
| `APP_COLLECTOR_CONTRACT_DIR` | one reviewed contract per platform, `<platformCode>.json`, mode 0600 |

Screenshots and videos show the screen as it was, including an account nickname or avatar.
They stay in the evidence directory and are never uploaded or committed. Do not open the
evidence directory while a batch runs.

Limits are counted from the ledger in the evidence directory. Always use the same directory;
a second directory starts a second ledger.

Optional settings (collection timeout, recording size and bit rate, Appium port, tool paths,
free-space floor) and their defaults are defined in `apps/collector/src/app-channel/env.ts`.

## Screen recording

`APP_COLLECTOR_RECORD_SCREEN` is `off` by default; set it to `on` to record every attempt.

- `off`: no recorder is started and `ffprobe` is not needed. A record's `video` is `null` and a
  valid sample needs the completion screenshot only. A recorder left on a device by an earlier
  run is not ended. The free-space floor is still checked before a batch.
- `on`: every attempt is recorded inside the emulator into `<answerId>.mp4`
  (`APP_COLLECTOR_RECORD_SIZE`, `APP_COLLECTOR_RECORD_BIT_RATE`), `ffprobe` checks the video, a
  question is not sent without a running recorder (`recording_failed`), and a sent question whose
  video is missing, unplayable or too short is `evidence_incomplete`. An App whose start screen
  is animated can stop responding while it is recorded (Kimi, driver-spike.md section 15).

Records written with recording on, including those of v1.0, are read as before by `run`
(resume), `report` and `prune`.

## Commands

```bash
pnpm collector:app:check
```

Read-only check of every binding: device online, declared properties, App installed, installed
version equals the reviewed one. With `APP_COLLECTOR_CONTRACT_DIR` set it also opens each App to
look for the logged-in marker, and it may tap "wait" on Android's "isn't responding" dialog.
Never run it with a contract directory while a batch is running: it opens Apps on the same
devices. With a contract directory, an enabled binding whose platform has no valid contract
fails as `app_contract_unverified`; disabled bindings are reported with their reason and do not
fail the check.

```bash
pnpm collector:app:run --batch <id> [--platform <code>]... [--device <avd>]... [--question <text>]... [--limit <n>]
```

Runs or resumes a batch. A question × platform cell that already has a valid record is not
asked again. Questions come from `COLLECTOR_APPROVED_QUESTIONS_FILE` (with
`COLLECTOR_TENANT_ID` and `COLLECTOR_EXPECTED_PROJECT_ID`), or the built-in four smoke
questions when the file is unset. The sent text is the question itself: the Web channel's
prefix is not added, and `sentText` in a record equals `questionText` (records of v1.0 hold the
prefixed text).

```bash
pnpm collector:app:report --batch <id>
```

The report counts, per platform, the valid samples by the state of their sources (`read`,
`absent`, `unreadable`, `not_declared`). Samples written before v1.1 have no such state and are
in none of the four.

```bash
pnpm collector:app:prune --days 30
```

Deletes videos past the retention period. Records without a video are left as they are.

Exit codes: 0 finished; 1 usage, configuration or environment problem; 2 for `check`, an enabled
binding fails the self-check, and for `run`, no binding in scope can work and nothing was
attempted; 3 `run` was stopped by a signal.

## Limits

Changed on 2026-11-17 (total PRD KD-21, values set by the user). Counted from the send ledger,
so across restarts.

| Level                      | Minimum interval | Hourly          | Daily           |
| -------------------------- | ---------------- | --------------- | --------------- |
| one binding (device × App) | 100 s            | none by default | none by default |
| one platform, both devices | 30 s             | -               | none by default |

`app-bindings.json` may set a longer interval (`minIntervalSeconds`, at least 60 s per binding
and 10 s per platform) and optional counts (`hourlyQuota`, `dailyQuota` per binding, `dailyCap`
per platform). The earlier rule that App limits may not be looser than the Web channel's no
longer applies to the App channel. In `serve` the control plane decides first; the larger of its
interval and this file's applies.

A question counts as sent from the moment before the send tap, also when the attempt then
fails. A rate-limit notice stops the binding for the rest of that day.

## Reviewing a contract

A contract describes one App version in one language: how to open a new conversation, the
composer, what marks a finished answer, how the answer and the sources are read, and which
screens block. The schema is `apps/collector/src/app-channel/contract.ts`; the repository holds
only synthetic fixtures.

- Review on the real App with an authorized account. A contract is valid for seven days and for
  the listed App versions only; after an App update the binding stops until the contract and
  `reviewedAppVersion` in the bindings file are reviewed again.
- Prefer resource IDs. A blocker marker that is only a text can match answer text. The same
  holds for a `panel` marker that is only a text: after a failed source read every path's
  panel marker is looked for, and one that also occurs in the answer fails the attempt.
- Leave a blocker list empty when the screen was never seen. Do not invent markers.
- A notice the App writes in place of an answer goes into `completion.notices`, not into
  `blockers`: `as: "rate_limited"` for an account that gets no more answers today,
  `as: "declined"` for a question the platform did not answer this time (a queue). Notices are
  looked for only while waiting for the answer to the question just sent, so the same words
  left in an older conversation never stop a binding. Match the whole notice with `textMatches`
  and `className`, not a word such as "upgrade" that an answer can contain. Kimi 3.1.8 writes
  both kinds as plain text views (11-19).
- Where the App states the number of sources, declare `citations.count`: a list read with a
  different length is then left out (`citationsStatus: "unreadable"`) instead of saved. The
  number is taken from the view's text, or from its content description where it has no text.
- An App that draws its own screen (Zhipu Qingyan 3.8.7) gives its views no resource ID, puts
  every text into the content description, and shows its controls as image views without any
  label. Four selector conditions are for such screens:
  - `descMatches`: as `textMatches`, for the content description. Use it instead of
    `descContains` for anything that could also be a sentence: the answer is in the
    descriptions too, and "这3个来源" contains "个来源".
  - `clickable` (`true` or `false`): whether the view reacts to a tap. It tells a switch from a
    table cell with the same words.
  - `unlabeled: true`: the view has no text, no content description and no resource ID. It
    cannot be combined with an `id`, text or description condition.
  - `row: { "size": n, "at": k }` (`size` 2 to 6, `at` from 0): of the views that meet the
    selector's other conditions, those with the same top and bottom edge form a row; the view
    is the `k`-th from the left of a row of exactly `n`. A row of one more or one fewer does not
    match. "The first of the three icons below the answer" is
    `{ "className": "android.widget.ImageView", "unlabeled": true, "clickable": true, "row": { "size": 3, "at": 0 }, "pick": "last" }`.

  `unlabeled` and `row` need `className`. The device is asked for the views of that class (and
  `clickable`), and both conditions are then applied to what it returns, so such a lookup
  reads five attributes per candidate view; keep the class narrow. `row` cannot be used in
  `answer.end`, `itemTitle`, `searchQueries.after` or `searchQueries.exclude`.

  Look at every screen the selector is used on, not only the one it was written for: on
  Qingyan's home screen the first row of two icons is the composer's (attach and send), in a
  conversation it is the top bar's (new conversation and more). A step that taps "the first of
  a row of two" therefore carries `unless` with a view only the home screen shows.

- A text that the input reports back is not proof that the App has it. Qingyan's input takes
  a text only while it has the keyboard: set without it, the field's view reports the text
  while the App still shows its placeholder, and the icon at the right of the composer still
  switches to voice input. So `composer.prepare` taps the input first, and `composer.send` is
  what only the send icon is: the one image view that has no label and does not say that it
  takes a tap (`"clickable": false`, default `pick`). With the text missing there are several
  such views or none, and the attempt fails before anything is counted as sent. When reviewing
  a composer, type a text through the driver and look at the screen, not only at the readback.
- A selector that names one view (default `pick`) and finds several is given the step's time
  to find one, like a view that is not there yet; matches that stay are `selector_ambiguous`.
- Before a step that taps a view by class and place, wait for a labelled view that only the
  App's own screen shows (`{ "waitFor": S }`): such a selector fits a view of any screen, a
  login screen included, and Qingyan's contract declares no blocker markers.
- Where a source card is one view that describes itself (title, snippet lines and link as the
  lines of its content description), declare `cards.from: "desc"`. A slot with `many: true`
  takes every line between the slots before it and the slots after it, which are fitted from
  the end of the card: `[{ "name": "title" }, { "name": "snippet", "many": true, "optional": true }, { "name": "url", "pattern": "https?://\\S+" }]`.
  A template takes one such slot at most; it has no pattern, is not `title`, `site` or `url`,
  and every other optional slot of the template then needs a pattern. Keep the link required
  where every card shows one: a card without it does not fit, the count check then fails, and
  the list is left out instead of saved short.
- A selector can take `textMatches`: a regular expression that the whole text of the view must
  match, for a text with a changing part (`"使用 \\d+ 个工具.*"`). It is not a search inside
  the text. On the device the pattern is a Java one, with `.` also matching a line break;
  exported hierarchies are matched the same way. Keep to syntax that means the same in Java and
  JavaScript: `\d`, `\w` and `\b` differ for non-ASCII text, so write `[0-9]` for a digit. A
  pattern that does not compile, or that ends with a backslash, rejects the contract.
- `citations.entrySwipe` bounds the swipes that bring the source entry on screen, one at a time,
  with `max` up to 40. An entry at the start of a long answer needs many; an entry that is not
  reached is recorded as `absent`. The optional `stopWhenUnmoved` (2 to 10) ends the search
  once that many swipes in a row left the exported hierarchy unchanged. Leave it out where a
  whole answer sits in one text view, whose hierarchy stays the same while it scrolls (Doubao):
  there the search would stop inside the answer.
- Where the entry opens a screen in between instead of the source list (the tool steps of an
  answer), declare `citations.via`: `panel` (visible while that screen is open), `item` (the
  rows that lead to a source list), `close` (a control, or `{ "back": true }`), and optionally
  `queryPrefix` and `maxItems` (1 to 10, default 5). The rows are taken from the screen once it
  has come to rest, without scrolling it, each text once. Each is tapped as the view `item`
  finds with that text, after a look for a crash or a blocker; its list is
  read as `citations.panel` describes it and closed with `citations.close`, which must lead back
  to the screen in between. The lists are joined, and a source with the same title, site and URL
  is kept once. A row whose text starts with `queryPrefix` gives a search keyword (the rest of
  the text). No row means `absent`; at least one row sets the web-search state to `on`. `via`
  cannot be combined with `count`. A read that fails here uses the causes below:
  `source_panel_not_opened` when either screen does not open, `source_panel_not_closed` when
  either does not close or a list does not lead back; the clean-up leaves the list first, then
  the screen in between, and no sample is saved while one of them is still on screen. Once
  the screen in between was opened, the end of the answer must be back on screen whatever the
  source status, else the attempt fails `answer_end_not_visible`. With `via`
  the record's sources are what the opened rows list, not what the answer cites inline.
- Where one answer shows several source lists behind different entries (the sources it cites
  at its end, the pages its search steps returned at its start), `citations` can be an array of
  2 to 3 paths instead of one object. Each path is a whole `citations` object (its own `entry`,
  `panel`, `close`, and `via`, `count` or `entrySwipe` where needed) plus a required `label`:
  a lowercase letter followed by lowercase letters, digits or `_`, at most 20 characters, unique
  in the array: `[{ "label": "cited", ... }, { "label": "search", ... }]`. A single object takes
  no `label`. The paths are read in the order written; put first the one whose entry is on
  screen when the answer completes. A path whose read fails is left out, its screens are
  closed, and the next path is still read. After a path that failed, or one that went through
  a screen in between (`via`), the end of the answer must be back on screen before the next
  path begins, else the attempt fails `answer_end_not_visible`; the next entry is then looked
  for from the end of the answer, so an entry elsewhere needs its own `entrySwipe`. In the record, `citations` holds the lists one after
  the other and every source carries `list` (the label of its path); a source that two lists
  show is kept once per list. `citationLists` has one `{ label, status, issue?, count }` per
  path, in the same order. The search keywords of all paths are merged, each once. A contract
  with a single `citations` object writes neither `list` nor `citationLists`, and keeps its
  keyword line as the panel shows it, a repeated keyword included.
- In a card template, `cards.sameHeight: true` states that the cards of the list have one
  usual height. A card less than nine tenths as tall as the height most cards of the same
  export have (counted among the cards that have a card on either side; all of them when the
  export holds one or two) is then the half card at an edge of the list and is not read. The usual height is
  the measure, not the tallest: real cards differ by a pixel of rounding and the first and last
  card of a list carry its padding (Kimi 3.1.8: 105 px, one of 104 px, 126 px at both ends).
  Declare it where the template has an optional slot: a half card showing only its title
  otherwise fits as a source without a site and is read again, whole, after the next swipe. Do
  not declare it for a list whose cards really differ in height: the shorter ones would never
  be read.
- Where the end of the answer has to be brought back after the sources were read from the
  start of the answer, `completion.reveal` must work from there. An App's jump-to-end button
  can be hidden while the screen has not moved for a while (Kimi 3.1.8), so swipe once before
  tapping it: `[{ "swipe": "up", "count": 1 }, { "tap": S, "optional": true, "timeoutMs": 1500 }]`.
- Where suggested follow-ups appear below the answer without an ID, declare `answer.end`.
- With the hierarchy read method, `answer.question` is required: what tells the bubble that
  shows the sent text from the answer's text views. Its conditions are `id`, `className` and
  `withoutDesc: true` (the view has no content description); it is compared with exported
  hierarchies only. A contract without it is rejected (`app_contract_unverified`) and nothing is
  sent. By its text alone an answer line that repeats the question could be taken for the bubble
  while the real one is scrolled off screen, and the saved answer would start after that line.
  Doubao 15.7.0 gives neither view a resource ID; its answer view carries its text as content
  description and the bubble has none, so its contract uses
  `{ "className": "android.widget.TextView", "withoutDesc": true }`.
- Bring the end of the answer on screen with the App's own scroll-to-end control when it has
  one; some answer controls sit under the App's shortcut bar until then.
- Do not send a question only to discover selectors more than once per App. A discovery
  attempt is recorded as a failed attempt and counts against the limits.

## Failure classes and what to do

| Class                                      | Meaning                                                          | Action                                                                                                                  |
| ------------------------------------------ | ---------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `device_offline`, `device_config_mismatch` | device gone, or time zone/language/version differ                | restart or correct the device, run `check`                                                                              |
| `app_crashed`                              | App left the foreground or stopped responding                    | usually host load; rerun when idle                                                                                      |
| `login_required`, `challenge_required`     | the App asks for login or verification                           | do it by hand in the emulator                                                                                           |
| `rate_limited`                             | the App showed a rate-limit notice                               | binding rests until the next day                                                                                        |
| `emulator_rejected`                        | the App refuses the emulator                                     | platform is marked unavailable; do not work around it                                                                   |
| `app_contract_unverified`                  | contract missing, stale, or for another version/language         | review the contract                                                                                                     |
| `app_contract_changed`                     | the screen is not what the contract describes                    | the log line `app_attempt_failure_cause` names the check; review with `failure.png`, `ui.xml` and the video if recorded |
| `collection_timeout`                       | no finished answer within the time limit                         | check `failure.png` and `ui.xml`, and the video if recorded                                                             |
| `recording_failed`                         | recording is on and the recorder did not start; nothing was sent | check the device                                                                                                        |

A `collection_timeout` whose `app_attempt_failure_cause` is `platform_declined` ended early on a
declared `declined` notice: the platform did not answer, the binding keeps working, and in
control-plane mode the task goes back to the queue for another device. A `rate_limited` with cause
`notice_rate_limited` came from a `rate_limited` notice after the send.

A record with status `evidence_incomplete` was sent but its completion screenshot could not be
taken or, with recording on, its video is not usable; the cell is collected again on the next
run.

Causes of `app_contract_changed` that protect the data rather than point at a changed screen:

| Cause                    | Meaning                                                                                                                                             |
| ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| `answer_empty_or_echo`   | nothing was read, or the answer is the question itself (spaces are not compared: Apps re-space the text they show)                                  |
| `clipboard_unchanged`    | the copy control gave the same text as the previous copy on that device: the tap probably missed                                                    |
| `answer_end_not_visible` | the end of the answer could not be brought on screen (also after a source read that failed and left its panel open), so nothing was copied or saved |
| `answer_list_not_moving` | scrolling back to the question changed nothing eight times in a row                                                                                 |

None of these saves a sample. Two identical answers in a row on one device (a canned refusal)
also end as `clipboard_unchanged`.

## Sources are best effort

The answer makes the sample. Every valid record says how its sources were read
(`citationsStatus`):

| Status         | Meaning                                                                                     |
| -------------- | ------------------------------------------------------------------------------------------- |
| `read`         | at least one source, and as many as the App states where the contract declares a count      |
| `absent`       | the answer shows no source entry, or the App states that there are none                     |
| `unreadable`   | there is an entry but its list could not be read whole; `citations` is empty, never partial |
| `not_declared` | the platform's contract has no source section (Qwen)                                        |

An `unreadable` record carries the cause in `citationsIssue`, and the log has one
`app_citations_unreadable` line with the same word:

| Cause                      | Meaning                                                                             |
| -------------------------- | ----------------------------------------------------------------------------------- |
| `source_panel_not_opened`  | the entry was tapped but the panel did not show                                     |
| `source_list_empty`        | the panel opened but no source could be read                                        |
| `source_list_still_moving` | the list was still scrolling when the allowed swipes ran out                        |
| `citation_count`           | the number read differs from the number the App states; the log line carries both   |
| `source_panel_not_closed`  | the panel stayed open after the read                                                |
| `driver_<kind>`            | a driver command failed during the read; the log line names the command             |
| other words                | the contract check that failed during the read, for example `required_view_missing` |

These causes no longer refuse a sample. After such a read the script closes the panel if it is
still on screen (the contract's close control, then the back key, at most three presses) and
goes on. The attempt still fails when the panel is still on screen after that or shows up later,
or the end of the answer does not come back (`answer_end_not_visible` in all three cases; with
the panel open nothing is copied and no sample is saved, also when the answer controls are still
in the hierarchy under it), or the answer cannot be read. A login, verification, rate-limit or
emulator notice that shows during the source read keeps its own class and stops the binding as
before; the back key is not pressed once such a notice is on screen.

Search keywords are read with the sources, so a list that is `unreadable` contributes none.
With several lists the keywords of the lists that were read are kept.

Where the contract declares several source lists (an array of `citations` paths), each list has
its own status in `citationLists`: `read`, `absent` or `unreadable`, with the cause in `issue`
and `count` 0 for a list that was not read. `citationsStatus` then describes all of them
together: `read` when any list was read, else `unreadable` when any was (`citationsIssue` is
the cause of the first such list), else `absent`. So a `read` record can still have a list that
is `unreadable`: look at `citationLists` before comparing source counts. The
`app_citations_unreadable` log line names the list in `list`. The clean-up after a failed list
closes that list's screens only, and the checks above hold for the screens of every list: a
crash or blocker fails the attempt and stops the remaining lists, and no sample is saved while
a screen of any list is still open. The report counts by `citationsStatus` only. A failed
record has neither field; sources it had read by then keep their `list`.

## After an interruption

- A process killed hard can leave `.collector.lock` in the evidence directory and an Appium
  process behind. Check that no `app-collect` or `appium` process runs, then remove the lock
  file by hand.
- With recording on, a recorder left on a device is ended by the next attempt on that device.
- A source panel left open by a failed attempt can cover the App's controls; press back in the
  emulator until the conversation shows.
