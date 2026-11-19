import { z } from "zod";
import type { CollectionPhase } from "../platform-adapter.ts";

// App-channel failure classes (design §10). They are local to this slice: the control plane's
// `collectorFailureCodeSchema` in @aivc/contracts is not extended.
export const appFailureCodeSchema = z.enum([
  "device_offline",
  "device_config_mismatch",
  "app_crashed",
  "login_required",
  "challenge_required",
  "rate_limited",
  "emulator_rejected",
  "app_contract_changed",
  "app_contract_unverified",
  "collection_timeout",
  "recording_failed",
]);
export type AppFailureCode = z.infer<typeof appFailureCodeSchema>;

export const appCollectionPhaseSchema = z.enum(["pre_send", "post_send"]);

// A classified failure of one binding or one collection attempt.
export class AppChannelError extends Error {
  constructor(
    readonly code: AppFailureCode,
    readonly phase?: CollectionPhase,
    // Which check produced the class (for logs): a fixed word, never text read from a screen.
    readonly cause?: string,
    // Numbers that explain the cause (for logs), for example how many sources were stated
    // and how many were read.
    readonly numbers?: Readonly<Record<string, number>>,
  ) {
    super(code);
    this.name = "AppChannelError";
  }
}

// Problems that stop the script before any binding is worked on. They are never written to a
// record; the entry prints the code (and validator paths) and exits.
export type AppSetupCode =
  | "invalid_command"
  | "environment_invalid"
  | "bindings_invalid"
  | "adb_unavailable"
  | "evidence_unavailable"
  | "evidence_corrupt"
  | "disk_space_low"
  // The approved-question file is missing, not private, malformed or bound to another project.
  | "approved_questions_invalid"
  // The local Appium server could not be started.
  | "appium_unavailable";
export interface AppSetupIssue {
  path: string;
  message: string;
}
export class AppSetupError extends Error {
  constructor(
    readonly code: AppSetupCode,
    readonly issues: AppSetupIssue[] = [],
  ) {
    super(code);
    this.name = "AppSetupError";
  }
}

// Paths and validator messages only; a file's values are never echoed.
export function setupIssues(error: z.ZodError): AppSetupIssue[] {
  return error.issues.map((issue) => ({
    path: issue.path.map(String).join("."),
    message: issue.message,
  }));
}
