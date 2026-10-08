import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

/**
 * The dev server proxies to the API so a browser session never has to know the port, and so the
 * bearer token is sent to one origin. In the container the same paths are proxied by nginx —
 * see web/nginx.conf — so `npm run dev` and `docker compose up` exercise identical routing.
 */
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:8080", changeOrigin: true },
      "/health": { target: "http://localhost:8080", changeOrigin: true },
    },
  },
  build: {
    outDir: "dist",
    sourcemap: false,
  },
});
