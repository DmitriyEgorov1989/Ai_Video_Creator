import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { validateRenderRequest } from "../src/validate.js";
import type { EditPlan, RenderRequest } from "../src/contracts.js";

function validRequest(): RenderRequest & { plan: EditPlan } {
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

function validV2Request(): Record<string, unknown> {
  const request = validRequest() as unknown as Record<string, unknown>;
  request.plan = {
    schemaVersion: 2,
    jobId: "job-1",
    variantId: "variant-1",
    revisionId: "revision-1",
    analysisRevisionId: "analysis-1",
    output: { width: 1080, height: 1920, frameRate: { numerator: 30, denominator: 1 }, durationFrames: 60 },
    source: { originalAssetId: "original-1", masterAssetId: "master-1" },
    video: { clips: [{ sourceAssetId: "master-1", source: { startMicroseconds: 0, endMicroseconds: 2_000_000 }, master: { startMicroseconds: 0, endMicroseconds: 2_000_000 }, output: { startFrame: 0, endFrame: 60 }, crop: { preset: "fill", focalX: 0.5, focalY: 0.5, zoom: 1 }, effects: [] }] },
    effects: [],
    captions: { words: [] },
    audio: { sourceGain: 1 },
    skills: [{ id: "visual-director", version: "1.0.0", status: "applied" }],
  };
  return request;
}

function validRenderV2Request(): Record<string, unknown> {
  return {
    schemaVersion: 2,
    jobId: "job-1",
    variantId: "variant-1",
    outputPath: "C:/data/output.mp4",
    encoding: { videoBitrateBitsPerSecond: 4_000_000, audioBitrateBitsPerSecond: 128_000 },
    assets: [
      { sourceAssetId: "master-1", videoPath: "C:/data/master-1.mp4", audioPath: "C:/data/master-1.m4a", frameRate: { numerator: 30, denominator: 1 } },
      { sourceAssetId: "master-2", videoPath: "C:/data/master-2.mp4", audioPath: null, frameRate: { numerator: 24, denominator: 1 } },
    ],
    plan: {
      schemaVersion: 3,
      jobId: "job-1",
      variantId: "variant-1",
      revisionId: "revision-1",
      analysisRevisionId: "analysis-1",
      output: { width: 1080, height: 1920, frameRate: { numerator: 30, denominator: 1 }, durationFrames: 60 },
      sources: [
        { originalAssetId: "original-1", masterAssetId: "master-1", durationMicroseconds: 1_000_000 },
        { originalAssetId: "original-2", masterAssetId: "master-2", durationMicroseconds: 1_250_000 },
      ],
      video: { clips: [
        { sourceAssetId: "master-1", source: { startMicroseconds: 0, endMicroseconds: 1_000_000 }, master: { startMicroseconds: 0, endMicroseconds: 1_000_000 }, output: { startFrame: 0, endFrame: 30 }, crop: { preset: "fill", focalX: 0.5, focalY: 0.5, zoom: 1 }, effects: [] },
        { sourceAssetId: "master-2", source: { startMicroseconds: 0, endMicroseconds: 1_250_000 }, master: { startMicroseconds: 0, endMicroseconds: 1_250_000 }, output: { startFrame: 30, endFrame: 60 }, crop: { preset: "fill", focalX: 0.5, focalY: 0.5, zoom: 1 }, effects: [] },
      ] },
      effects: [],
      captions: { words: [] },
      audio: { sourceGain: 1 },
      skills: [],
    },
  };
}

test("accepts a valid render request", () => {
  assert.deepEqual(validateRenderRequest(validRequest()), []);
});

test("accepts a V2 render request with source-specific video and audio", () => {
  assert.deepEqual(validateRenderRequest(validRenderV2Request()), []);
});

test("rejects V2 assets without videoPath and request-plan version mismatches", () => {
  const request = validRenderV2Request();
  const assets = request.assets as Array<Record<string, unknown>>;
  delete assets[0].videoPath;
  assert.ok(validateRenderRequest(request).some((item) => item.path === "assets[0].videoPath"));

  assets[0].videoPath = "C:/data/master-1.mp4";
  (request.plan as Record<string, unknown>).schemaVersion = 2;
  assert.ok(validateRenderRequest(request).some((item) => item.code === "contract_version_mismatch"));

  (request.plan as Record<string, unknown>).schemaVersion = 3;
  assets[1].audioPath = assets[0].audioPath;
  assert.ok(validateRenderRequest(request).some((item) => item.code === "audio_asset_duplicate"));
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
    video: { clips: [{ sourceAssetId: "master-1", source: { startMicroseconds: 0, endMicroseconds: 2_000_000 }, master: { startMicroseconds: 0, endMicroseconds: 2_000_000 }, output: { startFrame: 0, endFrame: 60 }, crop: { preset: "fill", focalX: 0.5, focalY: 0.5, zoom: 1 }, effects: [] }] },
    effects: [{ type: "speed", parameters: { rate: 1 } }],
    captions: { words: [{ text: "Hello", output: { startFrame: 0, endFrame: 20 } }] },
    audio: { sourceGain: 1 },
    skills: [{ id: "hook-builder", version: "1", status: "applied" }],
  };

  assert.deepEqual(validateRenderRequest(request), []);
});

test("accepts bounded Unicode caption groups and rejects more than two lines", () => {
  const request = validRequest() as unknown as Record<string, unknown>;
  request.plan = {
    schemaVersion: 2,
    jobId: "job-1",
    variantId: "variant-1",
    revisionId: "revision-1",
    analysisRevisionId: "analysis-1",
    output: { width: 1080, height: 1920, frameRate: { numerator: 30, denominator: 1 }, durationFrames: 60 },
    source: { originalAssetId: "original-1", masterAssetId: "master-1" },
    video: { clips: [{ sourceAssetId: "master-1", source: { startMicroseconds: 0, endMicroseconds: 2_000_000 }, master: { startMicroseconds: 0, endMicroseconds: 2_000_000 }, output: { startFrame: 0, endFrame: 60 }, crop: { preset: "fill", focalX: 0.5, focalY: 0.5, zoom: 1 }, effects: [] }] },
    effects: [],
    captions: {
      words: [],
      groups: [{ text: "Привет мир", words: [{ text: "Привет", output: { startFrame: 0, endFrame: 15 } }, { text: "мир", output: { startFrame: 15, endFrame: 30 } }], output: { startFrame: 0, endFrame: 30 }, maxLines: 2, highlight: "activeWord", animation: "fade", placement: "bottomSafe" }],
    },
    audio: { sourceGain: 1 },
    skills: [{ id: "caption-director", version: "1.0.0", status: "applied" }],
  };

  assert.deepEqual(validateRenderRequest(request), []);
  const plan = request.plan as { captions: { groups: Array<{ maxLines: number }> } };
  plan.captions.groups[0].maxLines = 3;
  assert.ok(validateRenderRequest(request).some((item) => item.code === "caption_group_invalid"));
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

test("rejects conflicting visual transitions and effects without applied skill provenance", () => {
  const request = validV2Request();
  const plan = request.plan as { effects: Array<Record<string, unknown>> };
  plan.effects = [
    { id: "transition-1", type: "visualTransition", output: { startFrame: 20, endFrame: 30 }, sourceSkillId: "visual-director", parameters: { amount: 0.2 } },
    { id: "transition-2", type: "visualTransition", output: { startFrame: 25, endFrame: 35 }, sourceSkillId: "visual-director", parameters: { amount: 0.2 } },
  ];

  assert.ok(validateRenderRequest(request).some((item) => item.code === "effect_conflict"));
  plan.effects = [
    { id: "zoom-1", type: "dynamicZoom", output: { startFrame: 0, endFrame: 30 }, sourceSkillId: "unknown-skill", parameters: { amount: 0.1 } },
  ];
  assert.ok(validateRenderRequest(request).some((item) => item.code === "effect_provenance"));
});

test("accepts bounded crop keyframes and rejects jumping trajectories", () => {
  const request = validV2Request();
  const plan = request.plan as { video: { clips: Array<{ crop: Record<string, unknown> }> } };
  plan.video.clips[0].crop = {
    preset: "fill",
    focalX: 0.5,
    focalY: 0.5,
    zoom: 1,
    fallback: "center",
    keyframes: [
      { outputFrame: 0, focalX: 0.5, focalY: 0.5, zoom: 1 },
      { outputFrame: 30, focalX: 0.65, focalY: 0.5, zoom: 1.1 },
    ],
  };

  assert.deepEqual(validateRenderRequest(request), []);
  plan.video.clips[0].crop.keyframes = [
    { outputFrame: 0, focalX: 0.1, focalY: 0.5, zoom: 1 },
    { outputFrame: 1, focalX: 0.9, focalY: 0.5, zoom: 1 },
  ];
  assert.ok(validateRenderRequest(request).some((item) => item.code === "crop_jump"));
});

test("permits ducking only for a registered music asset", () => {
  const request = validV2Request();
  const plan = request.plan as { audio: Record<string, unknown> };
  plan.audio = { sourceGain: 1, fadeInFrames: 6, fadeOutFrames: 6, targetLoudnessLufs: -16, truePeakDbtp: -1, denoise: false, ducking: 0.5 };
  assert.ok(validateRenderRequest(request).some((item) => item.code === "audio_ducking_invalid"));

  plan.audio.musicAssetId = "music-1";
  assert.ok(validateRenderRequest(request).some((item) => item.code === "asset_unknown"));

  const assets = request.assets as Array<Record<string, unknown>>;
  assets.push({ id: "music-1", path: "C:/data/music.m4a" });
  assert.deepEqual(validateRenderRequest(request), []);
});
