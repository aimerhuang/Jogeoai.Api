import { loadWorkerConfig } from "@aivc/config";
import {
  FIXTURE_IDS,
  createDatabase,
  currentShanghaiDate,
  generateDailyTasks,
  migrateDatabase,
  seedFixture,
} from "@aivc/database";

const config = loadWorkerConfig();
const database = createDatabase(config.DATABASE_URL);
let stopping = false;

async function schedule(): Promise<void> {
  const scheduledDate = config.SCHEDULE_DATE ?? currentShanghaiDate();
  const result = await generateDailyTasks(database, {
    tenantId: FIXTURE_IDS.tenantId,
    brandProjectId: FIXTURE_IDS.brandProjectId,
    scheduledDate,
  });
  console.log(
    JSON.stringify({
      level: "info",
      service: "worker",
      event: "scheduled",
      scheduledDate,
      ...result,
    }),
  );
}

async function shutdown(): Promise<void> {
  stopping = true;
  await database.end();
  process.exit(0);
}

process.on("SIGINT", () => void shutdown());
process.on("SIGTERM", () => void shutdown());

await migrateDatabase(database);
await seedFixture(database);
await schedule();
if (config.WORKER_RUN_ONCE === "true") {
  await database.end();
} else {
  const timer = setInterval(() => {
    if (!stopping) {
      void schedule().catch(() => {
        console.error(
          JSON.stringify({ level: "error", service: "worker", event: "schedule_failed" }),
        );
      });
    }
  }, config.WORKER_INTERVAL_MS);
  void timer;
}
