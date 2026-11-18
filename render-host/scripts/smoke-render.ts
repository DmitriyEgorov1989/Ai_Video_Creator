import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { renderRequest } from "../src/render.js";
import type { RenderRequest } from "../src/contracts.js";

const execFileAsync = promisify(execFile);
const directory = resolve(".smoke");
const source = join(directory, "source.mp4");
const output = join(directory, "output.mp4");

await rm(directory, { recursive: true, force: true });
await mkdir(directory, { recursive: true });
await execFileAsync("ffmpeg", ["-y", "-f", "lavfi", "-i", "color=c=0x304050:s=320x568:d=1", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source]);

const request: RenderRequest = {
  schemaVersion: 1,
  jobId: "smoke-job",
  variantId: "smoke-variant",
  outputPath: output,
  encoding: { videoBitrateBitsPerSecond: 4_000_000, audioBitrateBitsPerSecond: 128_000 },
  assets: [{ id: "segment-1", path: source }],
  plan: {
    schemaVersion: 1,
    jobId: "smoke-job",
    variantId: "smoke-variant",
    revisionId: "revision-1",
    analysisRevisionId: "analysis-1",
    seed: 1,
    output: { width: 1080, height: 1920, frameRate: { numerator: 30, denominator: 1 }, durationFrames: 30 },
    template: "dynamic",
    templateProps: { captions: "emphasis", theme: "dark", title: "Smoke" },
    video: { clips: [{ segmentId: "segment-1", mapping: {}, output: { startFrame: 0, endFrame: 30 }, crop: "centerCrop" }] },
    audio: { sourceClips: [{ segmentId: "segment-1", output: { startFrame: 0, endFrame: 30 }, gain: 1 }], musicClips: [] },
    captions: { items: [{ text: "Smoke render", output: { startFrame: 0, endFrame: 30 } }] },
    overlays: { items: [] },
  },
};

await writeFile(join(directory, "request.json"), JSON.stringify(request, null, 2));
await renderRequest(request);
console.error(`Smoke render completed: ${output}`);
