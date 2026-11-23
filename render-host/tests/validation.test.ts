import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { validateRenderRequest } from "../src/validate.js";
import type { RenderRequest } from "../src/contracts.js";

function validRequest(): RenderRequest {
  return {
    schemaVersion: 1,
    jobId: "job-1",
    variantId: "variant-1",
    outputPath: "C:/data/output.mp4",
    encoding: { videoBitrateBitsPerSecond: 4_000_000, audioBitrateBitsPerSecond: 128_000 },
    assets: [{ id: "master-1", path: "C:/data/master.mp4" }],
    plan: {
      schemaVersion: 1,
      jobId: "job-1",
      variantId: "variant-1",
      revisionId: "revision-1",
      analysisRevisionId: "analysis-1",
      seed: 42,
      output: { width: 1080, height: 1920, frameRate: { numerator: 30, denominator: 1 }, durationFrames: 60 },
      template: "dynamic",
      templateProps: { captions: "emphasis", theme: "dark", title: null },
      video: {
        clips: [{
          segmentId: "segment-1",
          mapping: {
            originalAssetId: "original-1",
            original: { startMicroseconds: 2_000_000, endMicroseconds: 4_000_000 },
            masterAssetId: "master-1",
            master: { startMicroseconds: 2_000_000, endMicroseconds: 4_000_000 },
          },
          output: { startFrame: 0, endFrame: 60 },
          crop: "centerCrop",
        }],
      },
      audio: { sourceClips: [], musicClips: [] },
      captions: { items: [] },
      overlays: { items: [] },
    },
  };
}

test("accepts a valid render request", () => {
  assert.deepEqual(validateRenderRequest(validRequest()), []);
});

test("rejects null collections and unknown enums without throwing", () => {
  const request = validRequest() as unknown as Record<string, unknown>;
  const plan = request.plan as Record<string, unknown>;
  plan.video = null;
  plan.templateProps = { captions: "unknown", theme: "dark" };
  assert.doesNotThrow(() => validateRenderRequest(request));
  const issues = validateRenderRequest(request);
  assert.ok(issues.some((item) => item.code === "video_empty"));
  assert.ok(issues.some((item) => item.code === "enum_unknown"));
});

test("rejects ranges outside the output timeline", () => {
  const request = validRequest();
  request.plan.video.clips[0].output.endFrame = 61;
  const issues = validateRenderRequest(request);
  assert.ok(issues.some((item) => item.code === "range_out_of_bounds"));
});

test("rejects a clip whose master is not backed by a registered asset", () => {
  const request = validRequest();
  request.plan.video.clips[0].mapping.masterAssetId = "unknown-master";
  const issues = validateRenderRequest(request);
  assert.ok(issues.some((item) => item.code === "asset_unknown"));
});

test("rejects a clip without typed source mapping", () => {
  const request = validRequest();
  request.plan.video.clips[0].mapping = {} as typeof request.plan.video.clips[0]["mapping"];

  const issues = validateRenderRequest(request);

  assert.ok(issues.some((item) => item.code === "mapping_invalid"));
});

test("rejects a plan whose identity differs from the render request", () => {
  const request = validRequest();
  request.plan.jobId = "another-job";
  request.plan.variantId = "another-variant";

  const issues = validateRenderRequest(request);

  assert.ok(issues.some((item) => item.code === "plan_job_id_mismatch"));
  assert.ok(issues.some((item) => item.code === "plan_variant_id_mismatch"));
});

test("validates the shared positive fixture", async () => {
  const fixture = JSON.parse(await readFile(resolve(process.cwd(), "../contracts/fixtures/valid-render-request.json"), "utf8"));

  assert.deepEqual(validateRenderRequest(fixture), []);
});

test("rejects the shared null and enum fixture without throwing", async () => {
  const fixture = JSON.parse(await readFile(resolve(process.cwd(), "../contracts/fixtures/invalid-null-and-enum.json"), "utf8"));

  assert.doesNotThrow(() => validateRenderRequest(fixture));
  const issues = validateRenderRequest(fixture);
  assert.ok(issues.some((item) => item.code === "video_empty"));
  assert.ok(issues.some((item) => item.code === "enum_unknown"));
});

test("accepts a valid V2 plan with explicit source ranges and skill metadata", () => {
  const request = validRequest() as unknown as Record<string, unknown>;
  request.plan = {
    schemaVersion: 2,
    jobId: "job-1",
    variantId: "variant-1",
    revisionId: "revision-1",
    analysisRevisionId: "analysis-1",
    output: { width: 1080, height: 1920, frameRate: { numerator: 30, denominator: 1 }, durationFrames: 60 },
    source: { originalAssetId: "original-1", masterAssetId: "master-1" },
    video: { clips: [{ sourceAssetId: "segment-1", source: { startMicroseconds: 0, endMicroseconds: 2_000_000 }, master: { startMicroseconds: 0, endMicroseconds: 2_000_000 }, output: { startFrame: 0, endFrame: 60 }, crop: { preset: "fill", focalX: 0.5, focalY: 0.5, zoom: 1 }, effects: [] }] },
    effects: [{ type: "speed", parameters: { rate: 1 } }],
    captions: { words: [{ text: "Hello", output: { startFrame: 0, endFrame: 20 } }] },
    audio: { sourceGain: 1 },
    skills: [{ id: "hook-builder", version: "1", status: "applied" }],
  };

  assert.deepEqual(validateRenderRequest(request), []);
});

test("rejects V2 executable effect payloads and unknown future versions", () => {
  const request = validRequest() as unknown as Record<string, unknown>;
  const plan = request.plan as Record<string, unknown>;
  plan.schemaVersion = 2;
  plan.effects = [{ type: "blur", parameters: {}, script: "return 1" }];
  assert.ok(validateRenderRequest(request).some((item) => item.code === "effect_code_forbidden"));

  plan.schemaVersion = 99;
  assert.ok(validateRenderRequest(request).some((item) => item.code === "schema_version"));
});
