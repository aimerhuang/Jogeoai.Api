import { createHash } from "node:crypto";
import { constants } from "node:fs";
import { lstat, open, readFile } from "node:fs/promises";
import { join } from "node:path";
import { z } from "zod";
import type { CollectorConfig } from "@aivc/config";
import type { LeasedTask } from "@aivc/contracts";
import { CollectorError } from "./platform-adapter.ts";

export const APPROVED_QUESTIONS = [
  "管道内窥镜",
  "优联科技",
  "ulinktek",
  "下水道内窥镜制造商",
] as const;
export type ApprovedQuestions = readonly string[];

const MAX_APPROVED_FILE_BYTES = 256 * 1024;
const approvedQuestionsFileSchema = z
  .object({
    version: z.literal(1),
    tenantId: z.uuid(),
    projectId: z.uuid(),
    approvedAt: z.iso.datetime({ offset: true }),
    questions: z
      .array(z.string().min(1).max(500))
      .min(1)
      .max(50)
      .refine((questions) => new Set(questions).size === questions.length),
  })
  .strict();

// Operator-approved exact question list for real mode. It is bound to one tenant/project and
// must be a private regular file (no symlink, owned by this user, no group/other bits), the same
// rule as the browser profile. Any failure stops the process before the first lease, so a bad
// file never burns a control-plane lease attempt. Unset keeps the built-in smoke list.
// Only these three settings are read, so the local App-channel script can reuse the loader
// without a full collector configuration.
export type ApprovedQuestionsConfig = Pick<
  CollectorConfig,
  "COLLECTOR_APPROVED_QUESTIONS_FILE" | "COLLECTOR_TENANT_ID" | "COLLECTOR_EXPECTED_PROJECT_ID"
>;
export async function loadApprovedQuestions(
  config: ApprovedQuestionsConfig,
): Promise<ApprovedQuestions> {
  const path = config.COLLECTOR_APPROVED_QUESTIONS_FILE;
  if (!path) return APPROVED_QUESTIONS;
  try {
    const linkStat = await lstat(path);
    if (linkStat.isSymbolicLink() || !linkStat.isFile()) throw new Error();
    // O_NOFOLLOW + fstat closes the swap window between lstat and read.
    const file = await open(path, constants.O_RDONLY | constants.O_NOFOLLOW);
    let text: string;
    try {
      const stat = await file.stat();
      if (
        !stat.isFile() ||
        stat.dev !== linkStat.dev ||
        stat.ino !== linkStat.ino ||
        stat.uid !== process.getuid?.() ||
        (stat.mode & 0o077) !== 0 ||
        stat.size > MAX_APPROVED_FILE_BYTES
      )
        throw new Error();
      text = await file.readFile("utf8");
    } finally {
      await file.close();
    }
    const approved = approvedQuestionsFileSchema.parse(JSON.parse(text));
    if (
      approved.tenantId !== config.COLLECTOR_TENANT_ID ||
      approved.projectId !== config.COLLECTOR_EXPECTED_PROJECT_ID
    )
      throw new Error();
    return Object.freeze([...approved.questions]);
  } catch {
    throw new CollectorError("task_not_approved");
  }
}
export function assertApprovedTask(
  task: LeasedTask,
  config: CollectorConfig,
  approved?: ApprovedQuestions,
): void {
  if (
    task.tenantId !== config.COLLECTOR_TENANT_ID ||
    task.region !== config.COLLECTOR_REGION ||
    !config.COLLECTOR_SUPPORTED_PLATFORMS.includes(task.platformCode)
  )
    throw new CollectorError("task_not_approved");
  if (config.COLLECTOR_RETRY_TASK_ID && task.id !== config.COLLECTOR_RETRY_TASK_ID)
    throw new CollectorError("task_not_approved");
  if (config.COLLECTOR_MODE !== "real") return;
  if (
    task.brandProjectId !== config.COLLECTOR_EXPECTED_PROJECT_ID ||
    task.platformEntryCode !== `${task.platformCode}.consumer_web` ||
    ![
      "doubao",
      "ernie",
      "zhipu_qingyan",
      "qwen",
      "kimi",
      "deepseek",
      "tencent_yuanbao",
      "chatgpt",
    ].includes(task.platformCode) ||
    task.language !== "zh-CN" ||
    task.channel !== "consumer_web"
  )
    throw new CollectorError("task_not_approved");
  // A configured file must have been loaded at startup; never fall back to the built-in list.
  if (config.COLLECTOR_APPROVED_QUESTIONS_FILE && !approved)
    throw new CollectorError("task_not_approved");
  const questions = approved ?? APPROVED_QUESTIONS;
  // Exact equality, no trimming or normalization.
  if (!questions.some((question) => question === task.questionText))
    throw new CollectorError("task_not_approved");
}
// `leaseVersion` is only written in profile mode (design §12.1); legacy lines omit it.
const attemptSchema = z.object({
  taskId: z.uuid(),
  questionHash: z.string().length(64),
  leaseVersion: z.number().int().positive().optional(),
});
// Profile mode (COLLECTOR_SESSION_PROFILE_ID set): the control plane's quotas govern throughput,
// so the ledger has no lifetime cap and the same question may be asked again on a later day.
function profileMode(config: CollectorConfig): boolean {
  return Boolean(config.COLLECTOR_SESSION_PROFILE_ID);
}
function ledgerPath(
  directory: string,
  config: CollectorConfig,
  platformCode: LeasedTask["platformCode"],
): string {
  const binding = createHash("sha256")
    .update(
      JSON.stringify([
        config.COLLECTOR_API_URL,
        config.COLLECTOR_TENANT_ID,
        config.COLLECTOR_EXPECTED_PROJECT_ID,
        // Preserve existing Doubao attempt ledgers; namespace the new platform.
        ...(platformCode === "doubao" ? [] : [platformCode]),
      ]),
    )
    .digest("hex");
  return join(directory, `attempts-${binding}.jsonl`);
}
async function readAttempts(path: string): Promise<z.infer<typeof attemptSchema>[]> {
  let text = "";
  try {
    text = await readFile(path, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ENOENT")
      throw new CollectorError("task_not_approved");
  }
  try {
    return text
      .split("\n")
      .filter(Boolean)
      .map((line) => attemptSchema.parse(JSON.parse(line)));
  } catch {
    throw new CollectorError("task_not_approved");
  }
}
// Real-mode pre-lease check. The lifetime ledger cap is known before a lease, so a full ledger
// (for example after an explicit retry consumed a slot) stops here instead of after the control
// plane has already counted one of the task's lease attempts. Retries keep bypassing the cap.
// Profile mode has no lifetime cap; only the profile directory is required.
export async function assertAttemptCapacity(
  directory: string | undefined,
  config: CollectorConfig,
): Promise<void> {
  if (!directory) throw new CollectorError("profile_unavailable");
  if (config.COLLECTOR_RETRY_TASK_ID || profileMode(config)) return;
  for (const platform of config.COLLECTOR_SUPPORTED_PLATFORMS) {
    const attempts = await readAttempts(ledgerPath(directory, config, platform));
    if (attempts.length >= config.COLLECTOR_MAX_TASKS_PER_RUN)
      throw new CollectorError("task_not_approved");
  }
}
// Caller holds the profile lock. Append+fsync BEFORE touching the prompt submit button.
// A crash consumes a slot conservatively; operator approval is required to retry it.
// Profile mode dedupes only on (taskId, leaseVersion): a crash after reserving leaves the lease to
// expire and the control plane re-leases the task with a new version, which may run (at worst one
// extra conversation, never a second sample: the control plane accepts one sample per task).
// Legacy lines without a lease version never match.
export async function reserveAttempt(
  directory: string,
  task: LeasedTask,
  config: CollectorConfig,
  approved?: ApprovedQuestions,
): Promise<void> {
  assertApprovedTask(task, config, approved);
  const path = ledgerPath(directory, config, task.platformCode);
  const attempts = await readAttempts(path);
  const questionHash = createHash("sha256").update(task.questionText).digest("hex");
  if (profileMode(config)) {
    if (attempts.some((item) => item.taskId === task.id && item.leaseVersion === task.leaseVersion))
      throw new CollectorError("task_not_approved");
    await appendAttempt(path, { taskId: task.id, questionHash, leaseVersion: task.leaseVersion });
    return;
  }
  if (
    (attempts.length >= config.COLLECTOR_MAX_TASKS_PER_RUN &&
      config.COLLECTOR_RETRY_TASK_ID !== task.id) ||
    attempts.some(
      (item) =>
        (item.taskId === task.id && config.COLLECTOR_RETRY_TASK_ID !== task.id) ||
        (item.questionHash === questionHash && config.COLLECTOR_RETRY_TASK_ID !== task.id),
    )
  ) {
    throw new CollectorError("task_not_approved");
  }
  await appendAttempt(path, { taskId: task.id, questionHash });
}
async function appendAttempt(path: string, attempt: z.infer<typeof attemptSchema>): Promise<void> {
  const file = await open(path, "a", 0o600);
  try {
    await file.writeFile(JSON.stringify(attempt) + "\n");
    await file.sync();
  } finally {
    await file.close();
  }
}
