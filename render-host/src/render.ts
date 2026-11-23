import { copyFile, mkdir, readFile, rename, rm, stat } from "node:fs/promises";
import { createReadStream } from "node:fs";
import { createServer, type Server } from "node:http";
import { fileURLToPath } from "node:url";
import { basename, dirname, join, resolve } from "node:path";
import { bundle } from "@remotion/bundler";
import { renderMedia, selectComposition } from "@remotion/renderer";
import type { RenderRequest } from "./contracts.js";
import { emit } from "./events.js";
import { RemotionRoot } from "./entry.js";
import { normalizeRenderRequest, validateAssetFiles, validateRenderRequest } from "./validate.js";

const renderHostRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

export async function readRenderRequest(path: string): Promise<RenderRequest> {
  const content = await readFile(resolve(path), "utf8");
  return JSON.parse(content) as RenderRequest;
}

export async function renderRequest(request: RenderRequest): Promise<void> {
  const structuralIssues = validateRenderRequest(request);
  if (structuralIssues.length > 0) throw new RenderHostError("request_invalid", formatIssues(structuralIssues));

  request = normalizeRenderRequest(request);

  const assetIssues = await validateAssetFiles(request);
  if (assetIssues.length > 0) throw new RenderHostError("asset_invalid", formatIssues(assetIssues));

  emit({ type: "progress", stage: "validation", percent: 100 });
  const outputPath = resolve(request.outputPath);
  await mkdir(dirname(outputPath), { recursive: true });
  const temporaryOutput = join(dirname(outputPath), `.${request.jobId}-${request.variantId}.rendering.mp4`);
  const assetDirectory = join(renderHostRoot, "public", "render-assets", `${request.jobId}-${request.variantId}`);
  await rm(temporaryOutput, { force: true });
  await rm(assetDirectory, { recursive: true, force: true });
  let assetServer: Server | undefined;

  try {
    await mkdir(assetDirectory, { recursive: true });
    const assetFiles = new Map<string, string>();
    const renderRequestWithUrls: RenderRequest = {
      ...request,
      assets: await Promise.all(request.assets.map(async (asset, index) => {
        const extension = basename(asset.path).match(/\.[a-zA-Z0-9]+$/)?.[0] ?? ".media";
        const fileName = `${index}-${asset.id.replace(/[^a-zA-Z0-9._-]/g, "_")}${extension}`;
        const targetPath = join(assetDirectory, fileName);
        await copyFile(asset.path, targetPath);
        assetFiles.set(`/${fileName}`, targetPath);
        return { ...asset, path: `__asset_url__/${fileName}` };
      })),
    };
    assetServer = createServer((incoming, response) => {
      const filePath = assetFiles.get(new URL(incoming.url ?? "/", "http://localhost").pathname);
      if (!filePath) {
        response.writeHead(404).end();
        return;
      }
      createReadStream(filePath).on("error", () => response.writeHead(500).end()).pipe(response);
    });
    await new Promise<void>((resolveServer) => assetServer?.listen(0, "127.0.0.1", resolveServer));
    const address = assetServer.address();
    if (!address || typeof address === "string") throw new RenderHostError("asset_server_failed", "Could not start the local asset server.");
    for (const asset of renderRequestWithUrls.assets) asset.path = `http://127.0.0.1:${address.port}${asset.path.replace("__asset_url__", "")}`;
    emit({ type: "progress", stage: "bundle", percent: 0 });
    const serveUrl = await withRemotionDiagnosticsOnStderr(() => bundle({
      entryPoint: join(renderHostRoot, "src", "entry.tsx"),
      rootDir: renderHostRoot,
      publicDir: join(renderHostRoot, "public"),
      webpackOverride: (config) => ({
        ...config,
        resolve: {
          ...config.resolve,
          extensionAlias: {
            ...config.resolve?.extensionAlias,
            ".js": [".ts", ".tsx", ".js"],
          },
        },
      }),
    }));
    emit({ type: "progress", stage: "bundle", percent: 100 });
    const composition = await withRemotionDiagnosticsOnStderr(() => selectComposition({ serveUrl, id: "dynamic", inputProps: renderRequestWithUrls as unknown as Record<string, unknown> }));
    const configuredComposition = {
      ...composition,
      width: request.plan.output.width,
      height: request.plan.output.height,
      fps: request.plan.output.frameRate.numerator / request.plan.output.frameRate.denominator,
      durationInFrames: request.plan.output.durationFrames,
    };
    emit({ type: "progress", stage: "render", percent: 0 });
    await withRemotionDiagnosticsOnStderr(() => renderMedia({
      composition: configuredComposition,
      serveUrl,
      codec: "h264",
      pixelFormat: "yuv420p",
      videoBitrate: toRemotionBitrate(request.encoding.videoBitrateBitsPerSecond),
      audioBitrate: toRemotionBitrate(request.encoding.audioBitrateBitsPerSecond),
      outputLocation: temporaryOutput,
      inputProps: renderRequestWithUrls as unknown as Record<string, unknown>,
      onProgress: ({ progress }) => emit({ type: "progress", stage: "render", percent: Math.round(progress * 100) }),
    }));
    const outputStats = await stat(temporaryOutput);
    if (outputStats.size <= 0) throw new RenderHostError("output_empty", "Render produced an empty file.");
    await rename(temporaryOutput, outputPath);
    emit({ type: "completed", outputPath, durationFrames: request.plan.output.durationFrames });
  } catch (error) {
    await rm(temporaryOutput, { force: true });
    throw error;
  } finally {
    if (assetServer) await new Promise<void>((resolveServer) => assetServer?.close(() => resolveServer()));
    await rm(assetDirectory, { recursive: true, force: true });
  }
}

export class RenderHostError extends Error {
  public constructor(public readonly code: string, message: string) {
    super(message);
  }
}

function formatIssues(issues: Array<{ code: string; path: string; message: string }>): string {
  return issues.map((item) => `${item.code} at ${item.path}: ${item.message}`).join("; ");
}

function toRemotionBitrate(bitsPerSecond: number): string {
  if (bitsPerSecond % 1_000_000 === 0) return `${bitsPerSecond / 1_000_000}M`;
  return `${Math.ceil(bitsPerSecond / 1_000)}K`;
}

async function withRemotionDiagnosticsOnStderr<T>(operation: () => Promise<T>): Promise<T> {
  const originalWrite = process.stdout.write;
  process.stdout.write = process.stderr.write.bind(process.stderr) as typeof process.stdout.write;
  try {
    return await operation();
  } finally {
    process.stdout.write = originalWrite;
  }
}
