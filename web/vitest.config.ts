import path from "node:path";
import { defineConfig } from "vitest/config";

// D7: covers pure logic modules only (nav-model.ts and similar) — no
// jsdom/React rendering setup, matching what these tests actually need.
export default defineConfig({
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
  },
});
