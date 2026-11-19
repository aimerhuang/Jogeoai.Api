import { describe, expect, it, vi } from "vitest";
import { createChallengeWaiter } from "./challenge-wait.ts";
import type { FailureCode } from "./platform-adapter.ts";

const sequence = (...values: (FailureCode | undefined)[]) => {
  const check = vi.fn(async () => (values.length > 1 ? values.shift() : values[0]));
  return check;
};

describe("challenge waiter", () => {
  it("fails immediately without waiting when the budget is 0", async () => {
    const extendDeadline = vi.fn();
    const onEvent = vi.fn();
    const check = sequence(undefined);
    await expect(
      createChallengeWaiter({ budgetMs: 0, extendDeadline, onEvent }).wait({
        phase: "pre_send",
        pollMs: 5,
        signal: new AbortController().signal,
        check,
      }),
    ).rejects.toMatchObject({ code: "challenge_required" });
    expect(check).not.toHaveBeenCalled();
    expect(extendDeadline).not.toHaveBeenCalled();
    expect(onEvent).not.toHaveBeenCalled();
  });
  it("extends by the budget first, then returns unused time so the net extension is the wait", async () => {
    let clock = 1_000;
    const extendDeadline = vi.fn();
    const onEvent = vi.fn();
    const waiter = createChallengeWaiter({
      budgetMs: 10_000,
      extendDeadline,
      onEvent,
      now: () => clock,
    });
    const check = vi.fn(async () => {
      clock += 300;
      return check.mock.calls.length < 3 ? ("challenge_required" as const) : undefined;
    });
    await expect(
      waiter.wait({ phase: "post_send", pollMs: 1, signal: new AbortController().signal, check }),
    ).resolves.toBeUndefined();
    expect(extendDeadline.mock.calls).toEqual([[10_000], [900 - 10_000]]);
    expect(onEvent.mock.calls).toEqual([
      ["challenge_wait_started", "post_send"],
      ["challenge_cleared", "post_send"],
    ]);
  });
  it("shares one budget across every challenge of the task", async () => {
    let clock = 0;
    const extendDeadline = vi.fn();
    const waiter = createChallengeWaiter({ budgetMs: 1_000, extendDeadline, now: () => clock });
    const clearsAfter = (ms: number) => async () => {
      clock += ms;
      return undefined;
    };
    const signal = new AbortController().signal;
    await waiter.wait({ phase: "pre_send", pollMs: 1, signal, check: clearsAfter(700) });
    const stuck = vi.fn(async () => {
      clock += 100;
      return "challenge_required" as const;
    });
    await expect(
      waiter.wait({ phase: "post_send", pollMs: 1, signal, check: stuck }),
    ).rejects.toMatchObject({ code: "challenge_required" });
    expect(stuck).toHaveBeenCalledTimes(3);
    expect(extendDeadline.mock.calls).toEqual([[1_000], [-300], [300], [0]]);
    await expect(
      waiter.wait({ phase: "post_send", pollMs: 1, signal, check: clearsAfter(1) }),
    ).rejects.toMatchObject({ code: "challenge_required" });
  });
  it("returns a different blocker that replaced the challenge without a cleared event", async () => {
    const onEvent = vi.fn();
    const waiter = createChallengeWaiter({ budgetMs: 1_000, extendDeadline: vi.fn(), onEvent });
    await expect(
      waiter.wait({
        phase: "pre_send",
        pollMs: 1,
        signal: new AbortController().signal,
        check: sequence("challenge_required", "login_required"),
      }),
    ).resolves.toBe("login_required");
    expect(onEvent.mock.calls).toEqual([["challenge_wait_started", "pre_send"]]);
  });
  it("ignores failing event reporters and stops on abort", async () => {
    const controller = new AbortController();
    const extendDeadline = vi.fn();
    const waiter = createChallengeWaiter({
      budgetMs: 60_000,
      extendDeadline,
      onEvent: () => {
        throw new Error("report failed");
      },
    });
    const check = vi.fn(async () => {
      controller.abort();
      return "challenge_required" as const;
    });
    await expect(
      waiter.wait({ phase: "pre_send", pollMs: 1, signal: controller.signal, check }),
    ).rejects.toThrow();
    expect(extendDeadline).toHaveBeenCalledTimes(2);
    expect(extendDeadline.mock.calls[0]).toEqual([60_000]);
  });
});
