import { createHash } from "node:crypto";
import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const outputRoot = path.resolve(webRoot, "..", "wwwroot");
const excludedDirectories = new Set(["node_modules", "dist"]);

async function collectFiles(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && excludedDirectories.has(entry.name)) continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...await collectFiles(fullPath));
    else if (entry.isFile()) files.push(fullPath);
  }
  return files;
}

const files = (await collectFiles(webRoot)).sort();
const lines = [];
for (const file of files) {
  const relative = path.relative(webRoot, file).split(path.sep).join("/");
  const digest = createHash("sha256").update(await readFile(file)).digest("hex");
  lines.push(`${relative}\t${digest}`);
}
const sourceHash = createHash("sha256").update(`${lines.join("\n")}\n`).digest("hex");
await mkdir(outputRoot, { recursive: true });
await writeFile(path.join(outputRoot, ".pizzawave-source-hash"), `${sourceHash}\n`, "utf8");
