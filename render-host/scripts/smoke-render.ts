import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";
import assert from "node:assert/strict";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { renderRequest } from "../src/render.js";
import type { RenderRequest } from "../src/contracts.js";

const execFileAsync = promisify(execFile);
const directory = resolve(".smoke");
const sourceA = join(directory, "source-a.mp4");
const sourceB = join(directory, "source-b.mp4");
const output = join(directory, "output.mp4");

await rm(directory, { recursive: true, force: true });
await mkdir(directory, { recursive: true });
await execFileAsync("ffmpeg", ["-y", "-f", "lavfi", "-i", "color=c=red:s=320x568:d=1", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", sourceA]);
await execFileAsync("ffmpeg", ["-y", "-f", "lavfi", "-i", "color=c=blue:s=320x568:d=2", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=1", "-f", "lavfi", "-i", "sine=frequency=1320:sample_rate=48000:duration=1", "-filter_complex", "[1:a][2:a]concat=n=2:v=0:a=1[a]", "-map", "0:v", "-map", "[a]", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", sourceB]);

const request: RenderRequest = {
  schemaVersion: 2,
  jobId: "smoke-job",
  variantId: "smoke-variant",
  outputPath: output,
  encoding: { videoBitrateBitsPerSecond: 4_000_000, audioBitrateBitsPerSecond: 128_000 },
  assets: [
    { sourceAssetId: "master-1", videoPath: sourceA, audioPath: sourceA, frameRate: { numerator: 25, denominator: 1 } },
    { sourceAssetId: "master-2", videoPath: sourceB, audioPath: sourceB, frameRate: { numerator: 25, denominator: 1 } },
  ],
  plan: {
    schemaVersion: 3,
    jobId: "smoke-job",
    variantId: "smoke-variant",
    revisionId: "revision-1",
    analysisRevisionId: "analysis-1",
    output: { width: 320, height: 568, frameRate: { numerator: 30, denominator: 1 }, durationFrames: 60 },
    sources: [
      { originalAssetId: "original-1", masterAssetId: "master-1", durationMicroseconds: 1_000_000 },
      { originalAssetId: "original-2", masterAssetId: "master-2", durationMicroseconds: 2_000_000 },
    ],
    video: { clips: [
      { sourceAssetId: "master-1", source: { startMicroseconds: 0, endMicroseconds: 1_000_000 }, master: { startMicroseconds: 0, endMicroseconds: 1_000_000 }, output: { startFrame: 0, endFrame: 30 }, crop: { preset: "fill", focalX: 0.5, focalY: 0.5, zoom: 1 }, effects: [] },
      { sourceAssetId: "master-2", source: { startMicroseconds: 0, endMicroseconds: 1_000_000 }, master: { startMicroseconds: 0, endMicroseconds: 1_000_000 }, output: { startFrame: 30, endFrame: 60 }, crop: { preset: "fill", focalX: 0.5, focalY: 0.5, zoom: 1 }, effects: [] },
    ] },
    effects: [],
    captions: { words: [] },
    audio: { sourceGain: 1 },
    skills: [],
  },
};

await writeFile(join(directory, "request.json"), JSON.stringify(request, null, 2));
await renderRequest(request);
const probe = JSON.parse((await execFileAsync("ffprobe", ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", output])).stdout);
assert.equal(probe.streams.filter((stream: { codec_type: string }) => stream.codec_type === "video").length, 1);
assert.equal(probe.streams.filter((stream: { codec_type: string }) => stream.codec_type === "audio").length, 1);
assert.ok(Number(probe.format.duration) >= 1.9 && Number(probe.format.duration) <= 2.1);
await execFileAsync("ffmpeg", ["-v", "error", "-i", output, "-f", "null", "-"]);
const firstPixel = await readPixel(output, 0.25);
const secondPixel = await readPixel(output, 1.25);
assert.ok(firstPixel[0] > firstPixel[2] * 2, `Expected red first source, got ${firstPixel.join(",")}`);
assert.ok(secondPixel[2] > secondPixel[0] * 2, `Expected blue second source, got ${secondPixel.join(",")}`);
const first440 = await bandVolume(output, 0.1, 0.7, 440);
const first880 = await bandVolume(output, 0.1, 0.7, 880);
const second880 = await bandVolume(output, 1.1, 0.7, 880);
const removed1320 = await bandVolume(output, 1.1, 0.7, 1320);
assert.ok(first440 > first880 + 10, `Expected 440 Hz in first clip: ${first440} dB vs ${first880} dB`);
assert.ok(second880 > removed1320 + 10, `Expected 880 Hz and no removed 1320 Hz: ${second880} dB vs ${removed1320} dB`);
console.error(`Smoke render completed: ${output}`);

async function bandVolume(path: string, start: number, duration: number, frequency: number): Promise<number> {
  const result = await execFileAsync("ffmpeg", ["-v", "info", "-ss", String(start), "-t", String(duration), "-i", path, "-af", `bandpass=f=${frequency}:width_type=h:w=40,volumedetect`, "-f", "null", "-"]);
  const match = result.stderr.match(/mean_volume:\s*(-?[0-9.]+) dB/);
  assert.ok(match, `No volume measurement for ${frequency} Hz`);
  return Number(match[1]);
}

async function readPixel(path: string, seconds: number): Promise<Buffer> {
  return await new Promise((resolvePixel, reject) => {
    const process = spawn("ffmpeg", ["-v", "error", "-ss", String(seconds), "-i", path, "-vf", "scale=1:1", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], { stdio: ["ignore", "pipe", "pipe"] });
    const outputChunks: Buffer[] = [];
    const errorChunks: Buffer[] = [];
    process.stdout.on("data", (chunk: Buffer) => outputChunks.push(chunk));
    process.stderr.on("data", (chunk: Buffer) => errorChunks.push(chunk));
    process.on("error", reject);
    process.on("close", (code) => code === 0
      ? resolvePixel(Buffer.concat(outputChunks))
      : reject(new Error(Buffer.concat(errorChunks).toString("utf8"))));
  });
}
