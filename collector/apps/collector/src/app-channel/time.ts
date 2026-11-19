import { z } from "zod";

// Asia/Shanghai has been a fixed UTC+8 without daylight saving since 1991, so the business
// calendar is plain offset arithmetic and does not depend on the host's time-zone database.
const SHANGHAI_OFFSET_MS = 8 * 3_600_000;
const DAY_MS = 24 * 3_600_000;

const shifted = (date: Date) => new Date(date.getTime() + SHANGHAI_OFFSET_MS).toISOString();

// "2026-11-15T20:00:00.000+08:00"
export function toShanghaiIso(date: Date): string {
  return shifted(date).replace("Z", "+08:00");
}
// "2026-11-15": the Asia/Shanghai calendar day that "daily" limits are counted in.
export function shanghaiDay(date: Date): string {
  return shifted(date).slice(0, 10);
}
// First instant of the next Asia/Shanghai calendar day.
export function nextShanghaiMidnight(date: Date): Date {
  const local = date.getTime() + SHANGHAI_OFFSET_MS;
  return new Date(local - (((local % DAY_MS) + DAY_MS) % DAY_MS) + DAY_MS - SHANGHAI_OFFSET_MS);
}

// Every collection time is stored twice (PRD: UTC and Asia/Shanghai).
export const timestampPairSchema = z
  .object({
    utc: z.iso.datetime(),
    shanghai: z.iso.datetime({ offset: true }),
  })
  .strict()
  .refine(
    (value) =>
      value.shanghai.endsWith("+08:00") && Date.parse(value.shanghai) === Date.parse(value.utc),
    "utc and shanghai must be the same instant",
  );
export type TimestampPair = z.infer<typeof timestampPairSchema>;
export function timestampPair(date: Date): TimestampPair {
  return { utc: date.toISOString(), shanghai: toShanghaiIso(date) };
}

// Every wait in the flow and the scheduler goes through one of these, so tests run on a virtual
// clock. Resolves early (never rejects) when `signal` aborts.
export type Sleep = (ms: number, signal?: AbortSignal) => Promise<void>;
export const realSleep: Sleep = (ms, signal) =>
  new Promise((resolve) => {
    if (signal?.aborted) return resolve();
    const done = () => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", done);
      resolve();
    };
    const timer = setTimeout(done, Math.max(0, ms));
    signal?.addEventListener("abort", done, { once: true });
  });
