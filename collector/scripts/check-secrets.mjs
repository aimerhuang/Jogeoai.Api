import { readFile, readdir } from "node:fs/promises";
import { extname, join, relative } from "node:path";

const root = process.cwd();
const excludedDirectories = new Set([
  ".agents",
  ".claude",
  ".codex",
  ".git",
  ".trellis",
  "node_modules",
  "dist",
  "coverage",
]);
const allowedExtensions = new Set([".ts", ".js", ".mjs", ".json", ".yaml", ".yml", ".md", ".sql"]);
const secretPatterns = [
  /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/,
  /\bAKIA[0-9A-Z]{16}\b/,
  /\bgh[pousr]_[A-Za-z0-9_]{30,}\b/,
  /authorization\s*[:=]\s*["']Bearer [A-Za-z0-9._~-]{20,}["']/i,
  /cookie\s*[:=]\s*["'][^"']{20,}["']/i,
];

async function walk(directory) {
  const matches = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && excludedDirectories.has(entry.name)) continue;
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      matches.push(...(await walk(path)));
      continue;
    }
    if (!allowedExtensions.has(extname(entry.name))) continue;
    const content = await readFile(path, "utf8");
    for (const pattern of secretPatterns) {
      if (pattern.test(content)) matches.push(`${relative(root, path)} matched ${pattern.source}`);
    }
  }
  return matches;
}

const matches = await walk(root);
if (matches.length > 0) {
  console.error(matches.join("\n"));
  process.exit(1);
}
console.log("No committed secret patterns detected.");
