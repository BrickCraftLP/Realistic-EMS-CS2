// Bundles the REAL ems-panel.tsx / map3d.tsx / scss with the game's cs2/* modules replaced by mocks.
// Run from repo root: node prototype/panel/build.mjs
const path = require("path");
const UI = path.resolve(__dirname, "../../RealisticEMS/UI");
const mock = n => path.resolve(__dirname, "mocks", n);
module.exports = {
  mode: "development", devtool: false, stats: "errors-only",
  context: UI,
  entry: path.resolve(__dirname, "main.tsx"),
  output: { path: path.resolve(__dirname, "dist"), filename: "bundle.js", clean: true },
  resolve: {
    extensions: [".tsx", ".ts", ".js"], modules: [path.join(UI, "node_modules")],
    alias: { "cs2/api": mock("api.ts"), "cs2/ui": mock("ui.tsx"), "cs2/l10n": mock("l10n.ts"), "cs2/bindings": mock("bindings.ts") },
  },
  resolveLoader: { modules: [path.join(UI, "node_modules")] },
  module: { rules: [
    { test: /\.tsx?$/, loader: "ts-loader", options: { transpileOnly: true, configFile: path.join(UI, "tsconfig.json") } },
    { test: /\.s?css$/, use: ["style-loader", { loader: "css-loader", options: { modules: { auto: true, exportLocalsConvention: "camelCase", localIdentName: "[local]_[hash:base64:3]" } } }, "sass-loader"] },
  ] },
};
