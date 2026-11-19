import { execFile } from "node:child_process";
import { promisify } from "node:util";

const requiredServices = ["postgres", "api", "worker", "collector-cn", "collector-overseas"];
const execFileAsync = promisify(execFile);
const { stdout } = await execFileAsync(
  "docker",
  ["compose", "--env-file", ".env.example", "ps", "--format", "json"],
  { cwd: process.cwd() },
);
const services = stdout
  .trim()
  .split("\n")
  .filter(Boolean)
  .map((line) => JSON.parse(line));
for (const serviceName of requiredServices) {
  const service = services.find((candidate) => candidate.Service === serviceName);
  if (!service || service.State !== "running") {
    throw new Error(`${serviceName} is not running`);
  }
  if (["postgres", "api"].includes(serviceName) && service.Health !== "healthy") {
    throw new Error(`${serviceName} is not healthy`);
  }
}

const response = await fetch("http://127.0.0.1:4311/health");
if (!response.ok) {
  throw new Error(`API health check failed with HTTP ${response.status}`);
}
const health = await response.json();
if (health.status !== "ok") {
  throw new Error("API did not report healthy status");
}
console.log(`Healthy services: ${requiredServices.join(", ")}`);
