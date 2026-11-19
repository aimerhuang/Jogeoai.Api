import { setTimeout as delay } from "node:timers/promises";
import {
  type ChallengeWaiter,
  type ChallengeWaitRequest,
  type CollectionPhase,
  CollectorError,
  type FailureCode,
} from "./platform-adapter.ts";

export type ChallengeWaitEvent = "challenge_wait_started" | "challenge_cleared";

export interface ChallengeWaiterOptions {
  // Total wait allowed for one task across every challenge it meets. 0 disables waiting.
  budgetMs: number;
  // Moves the collection deadline. Positive before a wait, negative to return unused time.
  extendDeadline: (ms: number) => void;
  // Best-effort side effects (log, session event, notification). Failures are ignored.
  onEvent?: (event: ChallengeWaitEvent, phase: CollectionPhase) => Promise<void> | void;
  now?: () => number;
}

// One waiter per leased task. It only polls visibility through the caller's `check`; the
// operator resolves the challenge in the profile window. Net deadline extension equals the
// time actually waited, so a task can never exceed the collection timeout plus the budget.
export function createChallengeWaiter(options: ChallengeWaiterOptions): ChallengeWaiter {
  const now = options.now ?? Date.now;
  let remaining = Math.max(0, options.budgetMs);
  const emit = async (event: ChallengeWaitEvent, phase: CollectionPhase) => {
    try {
      await options.onEvent?.(event, phase);
    } catch {
      /* Reporting is best-effort and never changes the wait outcome. */
    }
  };
  return {
    async wait({ phase, pollMs, signal, check }: ChallengeWaitRequest) {
      if (remaining <= 0) throw new CollectorError("challenge_required");
      signal.throwIfAborted();
      const budget = remaining;
      const started = now();
      options.extendDeadline(budget);
      let outcome: FailureCode | "timeout" | "cleared" = "timeout";
      try {
        await emit("challenge_wait_started", phase);
        while (true) {
          const elapsed = now() - started;
          if (elapsed >= budget) break;
          await delay(Math.max(1, Math.min(pollMs, budget - elapsed)), undefined, { signal });
          const blocker = await check();
          if (blocker !== "challenge_required") {
            outcome = blocker ?? "cleared";
            break;
          }
        }
      } finally {
        const used = Math.min(budget, Math.max(0, now() - started));
        remaining -= used;
        options.extendDeadline(used - budget);
      }
      if (outcome === "timeout") throw new CollectorError("challenge_required");
      if (outcome !== "cleared") return outcome;
      await emit("challenge_cleared", phase);
      return undefined;
    },
  };
}
