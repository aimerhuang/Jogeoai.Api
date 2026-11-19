# App channel node: control-plane mode (`collector:app:serve`)

Explicit opt-in. Once an operator starts it, the node runs unattended: it leases App-channel tasks
from the Jogeoai control plane, asks each task's question in the App on the binding's emulator, and
submits the answer. Dispatch contract: `device-v1`, specified in
[the device dispatch design](../../.trellis/tasks/11-18-app-device-dispatch-retry/design.md).

Devices, `app-bindings.json`, operator-reviewed contracts, the evidence directory and failure
classes are the same as for the local trial: see [app-channel-local.md](app-channel-local.md).

## What it does and does not do

- It sends only the question text of a task it leased, as it is, in a new conversation. It never
  sends anything from the approved-question file.
- Login, CAPTCHA and device verification are recognised, never handled. The binding is reported
  (`login_required` / `challenge_required`) and the control plane stops it until an operator has
  dealt with it on the emulator and resumed the binding. What happens to the task is listed under
  [Failures](#failures).
- No fingerprint, stealth or humanized-input changes; no device property is changed.
- Screenshots, UI dumps and records stay in the local evidence directory. Only the screenshot's
  SHA-256 is submitted.
- Not done yet (first version): starting an emulator that is down, or restoring the language and
  time zone after an emulator restart. The binding is reported as `device_offline` until a person
  fixes it. A login probe also isn't part of the periodic self-check; a lost login shows up on the
  next question.

## Prerequisites

1. Drain old App nodes and reconcile their in-flight leases and pending results before
   switching. The Jogeoai API must support `device-v1` and its database must have migration
   `db/migrations/051_app_device_dispatch.sql` (per-attempt send history; no historical
   backfill). A binding report must advertise `device-v1` before the collector can
   lease. Do not mix old and new dispatchers on one queue.
2. As a system administrator, check `GET /api/v1/admin/collector-app/nodes`. For a new node,
   `POST /api/v1/admin/collector-app/nodes` with `{"nodeId":"<nodeId>"}`. The **201 response already
   contains `nodeId` and `token`**. Save these directly to a private env file outside Git (mode 600);
   do not print the response. For an existing node, reuse its saved token. Only call
   `POST /api/v1/admin/collector-app/nodes/{nodeId}/token` to replace a lost or compromised token
   or for planned rotation: the old token stops working immediately.
3. Set `Collector:AppTenantIds` in both the Jogeoai API and Worker for the intended tenant, and
   enable weekly App targets for the intended site. The node does not filter leases by brand or
   run. For a single-brand trial, verify that no other eligible App tasks or targets can feed it
   before starting `serve`. Newly enabled App targets first become due the next day; read back the
   plan's anchor and time zone to confirm the first slot.
4. At least one configured emulator for the intended platform is online. `collector:app:check`
   is an optional, operator-invoked diagnostic, not a startup or dispatch gate. Do not wrap `serve`
   in a script that requires every binding to pass a login/version check. Run the first authorized
   real collection in the foreground with an operator present.

## Environment

| Variable                        | Required | Notes                                                                                                    |
| ------------------------------- | -------- | -------------------------------------------------------------------------------------------------------- |
| `APP_COLLECTOR_API_URL`         | yes      | Jogeoai API, e.g. `http://127.0.0.1:5260`. Ports 4311 (Mock queue) and 4312 (fixture queue) are refused. |
| `APP_COLLECTOR_NODE_ID`         | yes      | The registered node ID.                                                                                  |
| `APP_COLLECTOR_NODE_TOKEN`      | yes      | Secret. Never in Git, logs or shell history.                                                             |
| `APP_COLLECTOR_EVIDENCE_DIR`    | yes      | Reuse the node's private directory outside Git with an `aivc/` component; its ledger survives restarts.  |
| `APP_COLLECTOR_CONTRACT_DIR`    | yes      | Operator-reviewed contracts.                                                                             |
| `APP_COLLECTOR_LEASE_SECONDS`   | no       | Default 180 (60–900); renewed every third of it.                                                         |
| `APP_COLLECTOR_HTTP_TIMEOUT_MS` | no       | Default 15000.                                                                                           |

Keep these in a private env file, for example `~/.config/aivc/app-node.env` (mode 600):

```bash
set -a; source ~/.config/aivc/app-node.env; set +a
export PATH=/opt/homebrew/opt/node@24/bin:$PATH
pnpm collector:app:serve
```

## Running it as a resident process (macOS)

A launchd agent restarts it after a crash or reboot. Example
`~/Library/LaunchAgents/com.aivc.app-node.plist` (adjust the paths):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>com.aivc.app-node</string>
  <key>ProgramArguments</key><array>
    <string>/bin/zsh</string><string>-c</string>
    <string>set -a; source ~/.config/aivc/app-node.env; set +a; export PATH=/opt/homebrew/opt/node@24/bin:$PATH; cd /Library/Project/Ai-visiblility-collector &amp;&amp; exec pnpm collector:app:serve</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ExitTimeOut</key><integer>660</integer>
  <key>StandardOutPath</key><string>/Users/YOU/Library/Logs/aivc-app-node.log</string>
  <key>StandardErrorPath</key><string>/Users/YOU/Library/Logs/aivc-app-node.log</string>
</dict></plist>
```

`launchctl bootstrap gui/$(id -u) <plist>` starts it; `launchctl bootout gui/$(id -u)/com.aivc.app-node`
stops it. `ExitTimeOut` gives the in-flight question its grace period (10 minutes) before launchd kills the process.

## What happens while it runs

- `bindings/report` registers configured routes and reports whether their emulator is online.
  Online is not proof of login or of a reviewed UI. The node does not open every App before
  dispatch. The assigned App and send control must still be identifiable before a question is sent.
- One serial loop per `(nodeId, deviceId)`; other devices can run in parallel. A device may
  serve different authorized brands and Apps. Each task retains its own brand, batch and platform.
- The API enforces one **100-second interval per device**, starting at the actual send. Time
  spent collecting the answer counts toward the interval. Taking a lease or failing before
  sending does not start the clock. If the send state is unknown the interval starts at the
  failure report; if an attempt ends with no report at all it starts when the lease ran out.
- A device gets a task when it is online, holds no other task and its interval is over. Binding
  intervals, platform intervals and daily question quotas are not used for device-v1 dispatch.
  Switches an operator or a safety stop has set still apply, each to its own binding or
  platform only: a stopped binding, a binding cooling down after a rate limit, a paused platform
  and a disabled platform entry.
- A device attempts a given task once. After a failure the task goes back to the queue for
  another device; with no other device it waits until its deadline. The same question can
  therefore really be sent once per device that has the App.
- Respect empty leases and their `retryAfterSeconds`. A busy or cooling device cannot switch
  Apps to start sooner; it does not delay another device. Without the control plane, do not send.
- Each answer is submitted with the screenshot hash. If the control plane is unreachable, the answer
  stays pending in `<evidence>/control-plane/tasks/` and is retried (every report cycle and at
  stop). A retry waits until the control plane has acknowledged the original device, platform and
  account label in this run. If `app_result_waiting_for_binding` appears after an account change,
  restore the original login and label before retrying. A late answer to an expired lease is
  `stale`: it is kept for audit and doesn't overwrite anything.

## Failures

A failure report carries the task ID, lease version, binding, failure code and send state. It
ends that one attempt. The task goes back to the queue and another device may ask it under a new
lease, whether the failure came before the send, after a confirmed send, or with the send state
unknown. The device that failed carries on with other tasks. A failure does not stop the device,
the platform or the batch, and a stale report must not release a newer lease.

The safety stops are the only exceptions:

| Screen or signal                                       | Binding                                                 | Task                                                    |
| ------------------------------------------------------ | ------------------------------------------------------- | ------------------------------------------------------- |
| Login required                                         | Stopped until an operator has logged in and resumed it. | Back to the queue, like any other failure.              |
| CAPTCHA, device verification, App refuses the emulator | Stopped until an operator has dealt with it.            | Closed as not collected. Never moved to another device. |
| Rate limit from the platform                           | Cools down until the end of the Beijing day.            | Closed as not collected. Never moved to another device. |

The platform breaker is unchanged: it pauses a platform when several of its bindings report
login or verification failures inside its window, and an operator resumes it.

A stopped binding is never resumed automatically, and nothing here permits handling a login,
CAPTCHA or device verification by automation.

There is no breaker for repeated ordinary failures. If an App's UI changes, that binding keeps
taking tasks and failing before send while the tasks move to other devices. The alerts view
(`GET /api/v1/admin/collector-app/alerts`) and the daily report list a binding whose last three
attempts all failed before send; disable it by hand. Nothing stops it automatically.

To trace one task, `GET /api/v1/admin/collector-app/tasks/{taskId}/attempts` returns its status
and outcome and, for every attempt, the device, account label, lease version, send state, send
time, failure code and whether its answer was the accepted one.

## Restart without asking twice

Keep the node's private task states, answer records and send ledger together. Recover by
**task ID and lease version**, not task ID alone:

| Local evidence                                               | Recovery                                                                                                                                                                                                         |
| ------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Full answer, upload not acknowledged                         | Replay the saved result with the original answer ID, lease and source. Do not ask again or put it under another device's lease. If the task has been leased again in the meantime the replay comes back `stale`. |
| Confirmed failure before send                                | Report `not_sent`. A different device may ask under a **new** lease of the same task.                                                                                                                            |
| Confirmed send at `sentAt`, failure before a complete answer | Report `sent` and the original timestamp. Only a new lease from the API can authorize another device to ask.                                                                                                     |
| Click may have happened, no confirmation                     | Report `unknown`. Do not click again in this attempt and do not report it as `not_sent`. The API may lease the task to another device.                                                                           |

A lease that runs out with no report counts as a failed attempt: the API may lease the task to
another device. Lease expiry is not evidence that no question was sent, so the device's interval
is counted from the end of that lease.

If a send remains unconfirmed, the device may wait up to 100 seconds after the last possible
send before taking a different task. Keep the evidence directory on a filesystem supporting
directory sync, and synchronize node and database clocks; investigate a rejected send timestamp
without converting it to `not_sent`.

For rollback, stop issuing new leases first. Drain or reconcile pending sends, answers and failures;
preserve the evidence directory, attempt history and settlements. Do not reset completed tasks.

Bindings that the old rules stopped after an ordinary failure are not resumed by the switch to
device-v1. Check each one and resume it by hand.

## Stopping

SIGINT / SIGTERM stops new leases and gives the question in flight up to 10 minutes to finish.
The node makes a final delivery attempt for pending answers and failure reports. If the API is
unreachable, keep the evidence directory for the next start; a clean exit alone does not confirm
that the control plane received a result.

## Logs

JSON lines with identifiers, codes and counts only, never question or answer text. Useful
events: `app_bindings_reported` (active / stopped / unhealthy bindings), `app_task_leased`,
`app_task_attempted`, `app_result_submitted` (`disposition`), `app_failure_reported`,
`app_lease_failed`, `app_result_submit_failed`, `app_node_stopped`.
