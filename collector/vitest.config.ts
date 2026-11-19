import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["packages/**/*.test.ts", "apps/**/*.test.ts"],
    exclude: ["**/node_modules/**", "tests/integration/**", "tests/infrastructure/**"],
    coverage: { reporter: ["text", "json-summary"] },
  },
});
