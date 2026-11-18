import { access } from "node:fs/promises";
import { isAbsolute, resolve } from "node:path";
import type { EditPlan, OutputRange, RenderRequest } from "./contracts.js";

export interface ValidationIssue {
  code: string;
  path: string;
  message: string;
}

const knownCrops = new Set(["fit", "centerCrop"]);
const knownCaptionPresets = new Set(["none", "minimal", "emphasis"]);
const knownThemes = new Set(["light", "dark"]);

function issue(code: string, path: string, message: string): ValidationIssue {
  return { code, path, message };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function requiredString(value: unknown, path: string, issues: ValidationIssue[]): value is string {
  if (typeof value !== "string" || value.length === 0) {
    issues.push(issue("required_string", path, "A non-empty string is required."));
    return false;
  }
  return true;
}

function validRange(value: unknown, path: string, duration: number, issues: ValidationIssue[]): value is OutputRange {
  if (!isRecord(value) || !Number.isInteger(value.startFrame) || !Number.isInteger(value.endFrame)) {
    issues.push(issue("range_invalid", path, "A range must contain integer startFrame and endFrame."));
    return false;
  }
  const startFrame = value.startFrame as number;
  const endFrame = value.endFrame as number;
  if (startFrame < 0 || endFrame <= startFrame || endFrame > duration) {
    issues.push(issue("range_out_of_bounds", path, "The range must be inside the output timeline."));
    return false;
  }
  return true;
}

function validatePlan(value: unknown, issues: ValidationIssue[]): value is EditPlan {
  if (!isRecord(value)) {
    issues.push(issue("plan_null", "plan", "Plan must be an object."));
    return false;
  }

  if (value.schemaVersion !== 1) issues.push(issue("schema_version", "plan.schemaVersion", "Only schema version 1 is supported."));
  for (const name of ["jobId", "variantId", "revisionId", "analysisRevisionId"]) requiredString(value[name], `plan.${name}`, issues);
  if (!Number.isInteger(value.seed)) issues.push(issue("seed_invalid", "plan.seed", "Seed must be an integer."));
  if (value.template !== "dynamic") issues.push(issue("template_unknown", "plan.template", "Only the dynamic template is supported."));

  const output = value.output;
  let duration = 0;
  if (!isRecord(output)) {
    issues.push(issue("output_null", "plan.output", "Output profile is required."));
  } else {
    for (const name of ["width", "height", "durationFrames"]) {
      if (!Number.isInteger(output[name]) || (output[name] as number) <= 0) issues.push(issue("output_invalid", `plan.output.${name}`, "Must be a positive integer."));
    }
    duration = typeof output.durationFrames === "number" ? output.durationFrames : 0;
    const frameRate = output.frameRate;
    const numerator = isRecord(frameRate) ? frameRate.numerator : undefined;
    const denominator = isRecord(frameRate) ? frameRate.denominator : undefined;
    if (!Number.isInteger(numerator) || !Number.isInteger(denominator) || (numerator as number) <= 0 || (denominator as number) <= 0) {
      issues.push(issue("frame_rate_invalid", "plan.output.frameRate", "A positive rational frame rate is required."));
    }
  }

  const props = value.templateProps;
  if (!isRecord(props)) {
    issues.push(issue("template_props_null", "plan.templateProps", "Template properties are required."));
  } else {
    if (typeof props.captions !== "string" || !knownCaptionPresets.has(props.captions)) issues.push(issue("enum_unknown", "plan.templateProps.captions", "Unknown caption preset."));
    if (typeof props.theme !== "string" || !knownThemes.has(props.theme)) issues.push(issue("enum_unknown", "plan.templateProps.theme", "Unknown color theme."));
    if (props.title !== undefined && props.title !== null && typeof props.title !== "string") issues.push(issue("title_invalid", "plan.templateProps.title", "Title must be a string or null."));
  }

  const video = value.video;
  const segmentIds = new Set<string>();
  if (!isRecord(video) || !Array.isArray(video.clips) || video.clips.length === 0) {
    issues.push(issue("video_empty", "plan.video.clips", "At least one video clip is required."));
  } else {
    video.clips.forEach((clip, index) => {
      const path = `plan.video.clips[${index}]`;
      if (!isRecord(clip)) {
        issues.push(issue("clip_invalid", path, "Video clip must be an object."));
        return;
      }
      if (requiredString(clip.segmentId, `${path}.segmentId`, issues)) segmentIds.add(clip.segmentId);
      if (!knownCrops.has(String(clip.crop))) issues.push(issue("enum_unknown", `${path}.crop`, "Unknown crop preset."));
      validRange(clip.output, `${path}.output`, duration, issues);
    });
  }

  const audio = value.audio;
  if (!isRecord(audio) || !Array.isArray(audio.sourceClips) || !Array.isArray(audio.musicClips)) {
    issues.push(issue("audio_invalid", "plan.audio", "Audio tracks must be initialized arrays."));
  } else {
    if (audio.musicClips.length !== 0) issues.push(issue("music_unsupported", "plan.audio.musicClips", "Music is not supported by the MVP."));
    audio.sourceClips.forEach((clip, index) => {
      if (!isRecord(clip)) {
        issues.push(issue("audio_clip_invalid", `plan.audio.sourceClips[${index}]`, "Audio clip must be an object."));
        return;
      }
      requiredString(clip.segmentId, `plan.audio.sourceClips[${index}].segmentId`, issues);
      validRange(clip.output, `plan.audio.sourceClips[${index}].output`, duration, issues);
      if (typeof clip.gain !== "number" || clip.gain < 0) issues.push(issue("gain_invalid", `plan.audio.sourceClips[${index}].gain`, "Gain must be a non-negative number."));
    });
  }

  const captions = value.captions;
  if (!isRecord(captions) || !Array.isArray(captions.items)) {
    issues.push(issue("captions_invalid", "plan.captions.items", "Caption items must be an initialized array."));
  } else {
    captions.items.forEach((caption, index) => {
      if (!isRecord(caption) || !requiredString(caption.text, `plan.captions.items[${index}].text`, issues)) return;
      validRange(caption.output, `plan.captions.items[${index}].output`, duration, issues);
    });
  }

  const overlays = value.overlays;
  if (!isRecord(overlays) || !Array.isArray(overlays.items)) issues.push(issue("overlays_invalid", "plan.overlays.items", "Overlay items must be an initialized array."));
  return issues.length === 0;
}

export function validateRenderRequest(value: unknown): ValidationIssue[] {
  const issues: ValidationIssue[] = [];
  if (!isRecord(value)) return [issue("request_null", "$", "Render request must be an object.")];
  if (value.schemaVersion !== 1) issues.push(issue("schema_version", "schemaVersion", "Only schema version 1 is supported."));
  requiredString(value.jobId, "jobId", issues);
  requiredString(value.variantId, "variantId", issues);
  requiredString(value.outputPath, "outputPath", issues);
  validatePlan(value.plan, issues);
  if (isRecord(value.plan)) {
    if (value.plan.jobId !== value.jobId) {
      issues.push(issue("plan_job_id_mismatch", "plan.jobId", "Plan jobId must match the render request jobId."));
    }
    if (value.plan.variantId !== value.variantId) {
      issues.push(issue("plan_variant_id_mismatch", "plan.variantId", "Plan variantId must match the render request variantId."));
    }
  }
  if (!Array.isArray(value.assets) || value.assets.length === 0) {
    issues.push(issue("assets_empty", "assets", "At least one local asset is required."));
  } else {
    const ids = new Set<string>();
    value.assets.forEach((asset, index) => {
      if (!isRecord(asset) || !requiredString(asset.id, `assets[${index}].id`, issues) || !requiredString(asset.path, `assets[${index}].path`, issues)) return;
      if (ids.has(asset.id)) issues.push(issue("asset_duplicate", `assets[${index}].id`, "Asset IDs must be unique."));
      ids.add(asset.id);
      if (!isAbsolute(asset.path)) issues.push(issue("asset_path_not_absolute", `assets[${index}].path`, "Asset paths must be absolute."));
    });
    const plan = isRecord(value.plan) ? value.plan : undefined;
    const video = plan && isRecord(plan.video) && Array.isArray(plan.video.clips) ? plan.video.clips : [];
    video.forEach((clip, index) => {
      if (isRecord(clip) && typeof clip.segmentId === "string" && !ids.has(clip.segmentId)) issues.push(issue("asset_unknown", `plan.video.clips[${index}].segmentId`, `No asset is registered for segment '${clip.segmentId}'.`));
    });
    const audio = plan && isRecord(plan.audio) && Array.isArray(plan.audio.sourceClips) ? plan.audio.sourceClips : [];
    audio.forEach((clip, index) => {
      if (isRecord(clip) && typeof clip.segmentId === "string" && !ids.has(clip.segmentId)) issues.push(issue("asset_unknown", `plan.audio.sourceClips[${index}].segmentId`, `No asset is registered for segment '${clip.segmentId}'.`));
    });
  }
  return issues;
}

export async function validateAssetFiles(request: RenderRequest): Promise<ValidationIssue[]> {
  const issues: ValidationIssue[] = [];
  for (const [index, asset] of request.assets.entries()) {
    try {
      await access(resolve(asset.path));
    } catch {
      issues.push(issue("asset_missing", `assets[${index}].path`, `Asset does not exist: ${asset.id}.`));
    }
  }
  return issues;
}
