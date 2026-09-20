import path from "node:path";
import { defineConfig } from "vitest/config";

// Node tests cover pure logic, route handlers and server-rendered React forms.
export default defineConfig({
  esbuild: { jsx: "automatic" },
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
