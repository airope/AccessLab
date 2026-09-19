import { defineConfig } from "vitest/config";
export default defineConfig({
  server: {
    proxy: {
      "/api": { target: "https://localhost:5443", secure: false },
      "/auth": { target: "https://localhost:5443", secure: false },
    },
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test-setup.ts"],
    restoreMocks: true,
  },
  build: { outDir: "dist", sourcemap: false },
});
