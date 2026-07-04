// Everywhere-specific vite config for the vendored open-connector web
// console. Mirrors upstream's config but sets:
//   base: "/connector-ui/"    — daemon Kestrel serves the SPA at this prefix
//   outDir: absolute path     — writes to 3rd/open-connector/dist/web/
//                                which the connector bundle target then
//                                copies into Resources/connector/web/.
// Do NOT edit the upstream 'web/src/**' tree; changes belong here.

import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [react()],
  base: "/connector-ui/",
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:7878",
      "/v1": "http://localhost:7878",
      "/openapi.json": "http://localhost:7878",
    },
  },
  build: {
    // Emit into 3rd/open-connector/dist/web/. build-connector-bundle.mjs
    // copies both dist/connector.bundle.js and dist/web/ into
    // Resources/connector/ during MSBuild.
    outDir: resolve(__dirname, "..", "dist", "web"),
    emptyOutDir: true,
  },
});
