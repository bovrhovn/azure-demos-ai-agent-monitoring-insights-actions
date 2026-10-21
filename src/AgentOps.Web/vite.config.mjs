import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
    plugins: [vue()],
    define: {
        "process.env.NODE_ENV": JSON.stringify("production"),
        __VUE_OPTIONS_API__: false,
        __VUE_PROD_DEVTOOLS__: false,
        __VUE_PROD_HYDRATION_MISMATCH_DETAILS__: false
    },
    build: {
        outDir: "wwwroot/js",
        emptyOutDir: false,
        lib: {
            entry: fileURLToPath(new URL("./Frontend/main.js", import.meta.url)),
            name: "AgentObservatory",
            formats: ["iife"],
            fileName: () => "app.js"
        }
    }
});
