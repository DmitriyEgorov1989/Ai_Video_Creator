import { access } from "node:fs/promises";
import { isAbsolute, resolve } from "node:path";
import type { OutputRange, RenderRequest, SourceMapping } from "./contracts.js";

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

function validSourceRange(value: unknown, path: string, issues: ValidationIssue[]): boolean {
  if (!isRecord(value)
    || !Number.isInteger(value.startMicroseconds)
    || !Number.isInteger(value.endMicroseconds)
    || (value.startMicroseconds as number) < 0
    || (value.endMicroseconds as number) <= (value.startMicroseconds as number)) {
    issues.push(issue("mapping_invalid", path, "A source range must contain increasing non-negative microsecond boundaries."));
    return false;
  }
  return true;
}

function validMapping(value: unknown, path: string, issues: ValidationIssue[]): value is SourceMapping {
  if (!isRecord(value)) {
    issues.push(issue("mapping_invalid", path, "Source mapping must be an object."));
    return false;
  }

  const originalAssetValid = requiredString(value.originalAssetId, `${path}.originalAssetId`, issues);
  const masterAssetValid = requiredString(value.masterAssetId, `${path}.masterAssetId`, issues);
  const originalRangeValid = validSourceRange(value.original, `${path}.original`, issues);
  const masterRangeValid = validSourceRange(value.master, `${path}.master`, issues);
  if (!originalAssetValid || !masterAssetValid || !originalRangeValid || !masterRangeValid) {
    issues.push(issue("mapping_invalid", path, "Source mapping must identify valid Original and Master ranges."));
    return false;
  }
  return true;
}

function validatePlan(value: unknown, issues: ValidationIssue[]): boolean {
  if (!isRecord(value)) {
    issues.push(issue("plan_null", "plan", "Plan must be an object."));
    return false;
  }

  if (value.schemaVersion === 2 || value.schemaVersion === 3 || value.schemaVersion === 4) {
    validatePlanV2(value, issues);
    return issues.length === 0;
  }
  if (value.schemaVersion !== 1) issues.push(issue("schema_version", "plan.schemaVersion", "Only schema versions 1, 2, 3 and 4 are supported."));
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
      validMapping(clip.mapping, `${path}.mapping`, issues);
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

function validatePlanV2(value: Record<string, unknown>, issues: ValidationIssue[]): void {
  for (const name of ["jobId", "variantId", "revisionId", "analysisRevisionId"]) requiredString(value[name], `plan.${name}`, issues);
  const output = value.output;
  let duration = 0;
  if (!isRecord(output)) {
    issues.push(issue("output_null", "plan.output", "Output profile is required."));
  } else {
    for (const name of ["width", "height", "durationFrames"]) {
      if (!Number.isInteger(output[name]) || (output[name] as number) <= 0) issues.push(issue("output_invalid", `plan.output.${name}`, "Must be a positive integer."));
    }
    duration = typeof output.durationFrames === "number" ? output.durationFrames : 0;
  }
  const sourceDurations = new Map<string, number>();
  if (value.schemaVersion === 3 || value.schemaVersion === 4) {
    const sources = value.sources;
    if (!Array.isArray(sources) || sources.length === 0) {
      issues.push(issue("source_invalid", "plan.sources", "At least one source identity is required."));
    } else {
      const originalIds = new Set<string>();
      sources.forEach((source, index) => {
        const path = `plan.sources[${index}]`;
        if (!isRecord(source)
          || !requiredString(source.originalAssetId, `${path}.originalAssetId`, issues)
          || !requiredString(source.masterAssetId, `${path}.masterAssetId`, issues)
          || !Number.isInteger(source.durationMicroseconds)
          || Number(source.durationMicroseconds) <= 0) {
          issues.push(issue("source_invalid", path, "Source identity and positive duration are required."));
          return;
        }
        if (originalIds.has(source.originalAssetId) || sourceDurations.has(source.masterAssetId)) {
          issues.push(issue("source_duplicate", path, "Original and Master source IDs must be unique."));
        }
        originalIds.add(source.originalAssetId);
        sourceDurations.set(source.masterAssetId, Number(source.durationMicroseconds));
      });
    }
  } else {
    const source = value.source;
    if (!isRecord(source)) issues.push(issue("source_invalid", "plan.source", "Source identity is required."));
    else {
      requiredString(source.originalAssetId, "plan.source.originalAssetId", issues);
      requiredString(source.masterAssetId, "plan.source.masterAssetId", issues);
    }
  }
  const video = value.video;
  if (!isRecord(video) || !Array.isArray(video.clips) || video.clips.length === 0) {
    issues.push(issue("video_empty", "plan.video.clips", "At least one video clip is required."));
  } else {
    let expectedStart = 0;
    video.clips.forEach((clip, index) => {
      const path = `plan.video.clips[${index}]`;
      if (!isRecord(clip)) { issues.push(issue("clip_invalid", path, "Video clip must be an object.")); return; }
      const sourceAssetValid = requiredString(clip.sourceAssetId, `${path}.sourceAssetId`, issues);
      validSourceRange(clip.source, `${path}.source`, issues);
      const masterRangeValid = validSourceRange(clip.master, `${path}.master`, issues);
      if ((value.schemaVersion === 3 || value.schemaVersion === 4) && sourceAssetValid) {
        const sourceDuration = sourceDurations.get(clip.sourceAssetId as string);
        if (sourceDuration === undefined) {
          issues.push(issue("source_unknown", `${path}.sourceAssetId`, "Clip must reference a declared Master source."));
        } else if (masterRangeValid && isRecord(clip.master) && Number(clip.master.endMicroseconds) > sourceDuration) {
          issues.push(issue("mapping_out_of_bounds", `${path}.master`, "Clip Master range exceeds its source duration."));
        }
      }
      if (!isRecord(clip.crop) || !["fit", "fill"].includes(String(clip.crop.preset))) issues.push(issue("enum_unknown", `${path}.crop.preset`, "Unknown crop preset."));
      else {
        const crop = clip.crop;
        if (![crop.focalX, crop.focalY].every((coordinate) => typeof coordinate === "number" && coordinate >= 0 && coordinate <= 1)
          || typeof crop.zoom !== "number" || crop.zoom < 1 || crop.zoom > 4) {
          issues.push(issue("crop_invalid", `${path}.crop`, "Crop coordinates and zoom are outside supported bounds."));
        }
        if (crop.fallback !== undefined && !["fit", "center"].includes(String(crop.fallback))) issues.push(issue("enum_unknown", `${path}.crop.fallback`, "Unknown crop fallback."));
        if (crop.keyframes !== undefined) {
          if (!Array.isArray(crop.keyframes)) {
            issues.push(issue("crop_keyframes_invalid", `${path}.crop.keyframes`, "Crop keyframes must be an array."));
          } else {
            let previous: Record<string, unknown> | undefined;
            crop.keyframes.forEach((keyframe, keyframeIndex) => {
              const keyframePath = `${path}.crop.keyframes[${keyframeIndex}]`;
              if (!isRecord(keyframe)
                || !Number.isInteger(keyframe.outputFrame)
                || Number(keyframe.outputFrame) < 0
                || Number(keyframe.outputFrame) >= duration
                || ![keyframe.focalX, keyframe.focalY].every((coordinate) => typeof coordinate === "number" && coordinate >= 0 && coordinate <= 1)
                || typeof keyframe.zoom !== "number" || keyframe.zoom < 1 || keyframe.zoom > 4) {
                issues.push(issue("crop_keyframes_invalid", keyframePath, "Crop keyframe values are outside supported bounds."));
                return;
              }
              if (previous) {
                if (Number(keyframe.outputFrame) <= Number(previous.outputFrame)) issues.push(issue("crop_keyframes_invalid", keyframePath, "Crop keyframes must be ordered by output frame."));
                if (Math.abs(Number(keyframe.focalX) - Number(previous.focalX)) > 0.150001
                  || Math.abs(Number(keyframe.focalY) - Number(previous.focalY)) > 0.150001) {
                  issues.push(issue("crop_jump", keyframePath, "Crop movement between keyframes exceeds the safe bound."));
                }
              }
              previous = keyframe;
            });
          }
        }
      }
      if (!Array.isArray(clip.effects)) issues.push(issue("effects_invalid", `${path}.effects`, "Clip effects must be an array."));
      const range = clip.output;
      if (validRange(range, `${path}.output`, duration, issues)) {
        if (range.startFrame !== expectedStart) issues.push(issue("video.timeline_not_contiguous", `${path}.output`, "Video clips must be contiguous."));
        expectedStart = range.endFrame;
      }
    });
    if (expectedStart !== duration) issues.push(issue("video.duration_mismatch", "plan.video.clips", "Video clips must end at output duration."));
  }
  const effects = value.effects;
  if (!Array.isArray(effects)) issues.push(issue("effects_invalid", "plan.effects", "Effects must be an array."));
  else {
    const enrichedTypes = new Set(["dynamicZoom", "textHighlight", "visualTransition"]);
    const appliedSkills = new Set<string>();
    if (Array.isArray(value.skills)) {
      value.skills.forEach((skill) => {
        if (isRecord(skill) && skill.status === "applied" && typeof skill.id === "string") appliedSkills.add(skill.id);
      });
    }
    const ids = new Set<string>();
    const transitions: Array<{ startFrame: number; endFrame: number }> = [];
    effects.forEach((effect, index) => {
      const path = `plan.effects[${index}]`;
      if (!isRecord(effect) || !["speed", "transition", "blur", ...enrichedTypes].includes(String(effect.type)) || !isRecord(effect.parameters)) {
        issues.push(issue("effect_unsupported", path, "Effect type and closed parameters are required."));
        return;
      }
      if (["code", "path", "script"].some((key) => key in effect)) issues.push(issue("effect_code_forbidden", path, "Effects cannot contain executable code or paths."));
      if (!enrichedTypes.has(String(effect.type))) return;
      if (!requiredString(effect.id, `${path}.id`, issues) || ids.has(String(effect.id))) {
        issues.push(issue("effect_identity", `${path}.id`, "Enriched effect IDs must be present and unique."));
      } else {
        ids.add(String(effect.id));
      }
      if (!requiredString(effect.sourceSkillId, `${path}.sourceSkillId`, issues) || !appliedSkills.has(String(effect.sourceSkillId))) {
        issues.push(issue("effect_provenance", `${path}.sourceSkillId`, "Effect must reference an applied skill."));
      }
      if (validRange(effect.output, `${path}.output`, duration, issues) && effect.type === "visualTransition" && isRecord(effect.output)) {
        transitions.push({ startFrame: Number(effect.output.startFrame), endFrame: Number(effect.output.endFrame) });
      }
    });
    transitions.sort((left, right) => left.startFrame - right.startFrame);
    for (let index = 1; index < transitions.length; index++) {
      if (transitions[index].startFrame < transitions[index - 1].endFrame) {
        issues.push(issue("effect_conflict", "plan.effects", "Visual transitions cannot overlap."));
      }
    }
  }
  const captions = value.captions;
  if (!isRecord(captions) || !Array.isArray(captions.words)) issues.push(issue("captions_invalid", "plan.captions.words", "Caption words must be an array."));
  else captions.words.forEach((word, index) => { if (!isRecord(word) || !requiredString(word.text, `plan.captions.words[${index}].text`, issues)) return; validRange(word.output, `plan.captions.words[${index}].output`, duration, issues); });
  if (isRecord(captions) && captions.groups !== undefined) {
    if (!Array.isArray(captions.groups)) {
      issues.push(issue("caption_group_invalid", "plan.captions.groups", "Caption groups must be an array."));
    } else {
      captions.groups.forEach((group, index) => {
        const path = `plan.captions.groups[${index}]`;
        if (!isRecord(group)
          || !requiredString(group.text, `${path}.text`, issues)
          || !Array.isArray(group.words)
          || group.words.length === 0
          || ![1, 2].includes(Number(group.maxLines))
          || !["none", "activeWord"].includes(String(group.highlight))
          || !["none", "fade"].includes(String(group.animation))
          || group.placement !== "bottomSafe") {
          issues.push(issue("caption_group_invalid", path, "Caption group must use bounded lines, highlight, animation and safe placement."));
          return;
        }
        validRange(group.output, `${path}.output`, duration, issues);
        group.words.forEach((word, wordIndex) => {
          if (!isRecord(word) || !requiredString(word.text, `${path}.words[${wordIndex}].text`, issues)) return;
          validRange(word.output, `${path}.words[${wordIndex}].output`, duration, issues);
        });
      });
    }
  }
  if (!isRecord(value.audio) || typeof value.audio.sourceGain !== "number" || value.audio.sourceGain < 0 || value.audio.sourceGain > 4) {
    issues.push(issue("audio_invalid", "plan.audio.sourceGain", "Source gain must be between 0 and 4."));
  } else {
    const audio = value.audio;
    if (audio.ducking !== undefined
      && (typeof audio.ducking !== "number" || audio.ducking < 0 || audio.ducking > 1 || typeof audio.musicAssetId !== "string" || audio.musicAssetId.length === 0)) {
      issues.push(issue("audio_ducking_invalid", "plan.audio.ducking", "Ducking requires a known music asset ID and a value from 0 to 1."));
    }
    if ([audio.fadeInFrames, audio.fadeOutFrames].some((frames) => frames !== undefined && (!Number.isInteger(frames) || Number(frames) < 0))) {
      issues.push(issue("audio_invalid", "plan.audio", "Audio fades must be non-negative frame counts."));
    }
    if ([audio.targetLoudnessLufs, audio.truePeakDbtp].some((level) => level !== undefined && (typeof level !== "number" || level > 0))) {
      issues.push(issue("audio_invalid", "plan.audio", "Loudness and true peak targets must not be positive."));
    }
  }
  if (!Array.isArray(value.skills)) issues.push(issue("skills_invalid", "plan.skills", "Applied skills must be an array."));
  else if (value.schemaVersion === 4) validateV4Skills(value.skills, issues);
  if (value.profile !== undefined && value.profile !== "instagram-reels") issues.push(issue("enum_unknown", "plan.profile", "Unknown output profile."));
  if (value.style !== undefined && (!isRecord(value.style)
    || !["auto", "expert-clean", "dynamic-viral", "cinematic", "premium-ads"].includes(String(value.style.requested))
    || !["expert-clean", "dynamic-viral", "cinematic", "premium-ads"].includes(String(value.style.resolved)))) {
    issues.push(issue("enum_unknown", "plan.style", "Unknown requested or resolved style."));
  }
}

const editPlanV4SkillIds = new Set([
  "multi-clip-director", "semantic-story", "hook-builder", "smart-cut",
  "best-shot", "pacing-director", "speech-tightening", "caption-director",
  "visual-director", "smart-reframe", "audio-director",
]);

function validateV4Skills(skills: unknown[], issues: ValidationIssue[]): void {
  const found = new Set<string>();
  skills.forEach((skill, index) => {
    const path = `plan.skills[${index}]`;
    if (!isRecord(skill)) {
      issues.push(issue("skill_invalid", path, "Each skill audit entry must be an object."));
      return;
    }
    const idValid = requiredString(skill.id, `${path}.id`, issues);
    requiredString(skill.version, `${path}.version`, issues);
    if (idValid) {
      const id = String(skill.id);
      if (!editPlanV4SkillIds.has(id)) issues.push(issue("skill_unknown", `${path}.id`, "Skill audit contains an unknown skill."));
      else if (found.has(id)) issues.push(issue("skill_duplicate", `${path}.id`, "Skill audit IDs must be unique."));
      else found.add(id);
    }
    if (!["applied", "skipped", "unavailable"].includes(String(skill.status))) {
      issues.push(issue("skill_status_invalid", `${path}.status`, "Skill status must be applied, skipped or unavailable."));
    }
    if (!Array.isArray(skill.reasons)) {
      issues.push(issue("skill_reasons_required", `${path}.reasons`, "Skill reasons must be an array."));
      return;
    }
    skill.reasons.forEach((reason, reasonIndex) => {
      if (!isRecord(reason)) {
        issues.push(issue("skill_reason_invalid", `${path}.reasons[${reasonIndex}]`, "Skill reason must be an object."));
        return;
      }
      requiredString(reason.code, `${path}.reasons[${reasonIndex}].code`, issues);
      requiredString(reason.message, `${path}.reasons[${reasonIndex}].message`, issues);
    });
  });
  editPlanV4SkillIds.forEach((id) => {
    if (!found.has(id)) issues.push(issue("skill_missing", "plan.skills", `Skill audit is missing '${id}'.`));
  });
}

export function validateRenderRequest(value: unknown): ValidationIssue[] {
  const issues: ValidationIssue[] = [];
  if (!isRecord(value)) return [issue("request_null", "$", "Render request must be an object.")];
  if (value.schemaVersion !== 1 && value.schemaVersion !== 2) issues.push(issue("schema_version", "schemaVersion", "Only schema versions 1 and 2 are supported."));
  requiredString(value.jobId, "jobId", issues);
  requiredString(value.variantId, "variantId", issues);
  requiredString(value.outputPath, "outputPath", issues);
  const encoding = value.encoding;
  if (!isRecord(encoding)) {
    issues.push(issue("encoding_missing", "encoding", "Encoding profile is required."));
  } else {
    if (!Number.isInteger(encoding.videoBitrateBitsPerSecond) || (encoding.videoBitrateBitsPerSecond as number) <= 0) {
      issues.push(issue("encoding_invalid", "encoding.videoBitrateBitsPerSecond", "Video bitrate must be a positive integer."));
    }
    if (!Number.isInteger(encoding.audioBitrateBitsPerSecond) || (encoding.audioBitrateBitsPerSecond as number) <= 0) {
      issues.push(issue("encoding_invalid", "encoding.audioBitrateBitsPerSecond", "Audio bitrate must be a positive integer."));
    }
  }
  validatePlan(value.plan, issues);
  if (isRecord(value.plan)) {
    if ((value.schemaVersion === 2) !== (value.plan.schemaVersion === 3 || value.plan.schemaVersion === 4)) {
      issues.push(issue("contract_version_mismatch", "plan.schemaVersion", "RenderRequest V2 requires Edit Plan V3 or V4; legacy plans require RenderRequest V1."));
    }
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
    const audioPaths = new Set<string>();
    value.assets.forEach((asset, index) => {
      if (!isRecord(asset)) {
        issues.push(issue("asset_invalid", `assets[${index}]`, "Asset must be an object."));
        return;
      }
      if (value.schemaVersion === 2) {
        if (!requiredString(asset.sourceAssetId, `assets[${index}].sourceAssetId`, issues)
          || !requiredString(asset.videoPath, `assets[${index}].videoPath`, issues)) return;
        if (ids.has(asset.sourceAssetId)) issues.push(issue("asset_duplicate", `assets[${index}].sourceAssetId`, "Source asset IDs must be unique."));
        ids.add(asset.sourceAssetId);
        if (!isAbsolute(asset.videoPath)) issues.push(issue("asset_path_not_absolute", `assets[${index}].videoPath`, "Video path must be absolute."));
        if (asset.audioPath !== undefined && asset.audioPath !== null
          && (typeof asset.audioPath !== "string" || asset.audioPath.length === 0 || !isAbsolute(asset.audioPath))) {
          issues.push(issue("asset_path_not_absolute", `assets[${index}].audioPath`, "Audio path must be null or an absolute path."));
        } else if (typeof asset.audioPath === "string") {
          if (audioPaths.has(asset.audioPath)) {
            issues.push(issue("audio_asset_duplicate", `assets[${index}].audioPath`, "Each source must own its audio path."));
          }
          audioPaths.add(asset.audioPath);
        }
        const frameRate = asset.frameRate;
        if (!isRecord(frameRate)
          || !Number.isInteger(frameRate.numerator) || Number(frameRate.numerator) <= 0
          || !Number.isInteger(frameRate.denominator) || Number(frameRate.denominator) <= 0) {
          issues.push(issue("frame_rate_invalid", `assets[${index}].frameRate`, "A positive rational frame rate is required."));
        }
      } else {
        if (!requiredString(asset.id, `assets[${index}].id`, issues) || !requiredString(asset.path, `assets[${index}].path`, issues)) return;
        if (ids.has(asset.id)) issues.push(issue("asset_duplicate", `assets[${index}].id`, "Asset IDs must be unique."));
        ids.add(asset.id);
        if (!isAbsolute(asset.path)) issues.push(issue("asset_path_not_absolute", `assets[${index}].path`, "Asset paths must be absolute."));
      }
    });
    const plan = isRecord(value.plan) ? value.plan : undefined;
    const video = plan && isRecord(plan.video) && Array.isArray(plan.video.clips) ? plan.video.clips : [];
    const videoSegmentIds = new Set<string>();
    const usedSourceIds = new Set<string>();
    video.forEach((clip, index) => {
      if (isRecord(clip) && typeof clip.segmentId === "string") videoSegmentIds.add(clip.segmentId);
      const mapping = isRecord(clip) && isRecord(clip.mapping) ? clip.mapping : undefined;
      if (mapping && typeof mapping.masterAssetId === "string" && !ids.has(mapping.masterAssetId)) {
        issues.push(issue("asset_unknown", `plan.video.clips[${index}].mapping.masterAssetId`, `No asset is registered for Master '${mapping.masterAssetId}'.`));
      }
      if (isRecord(clip) && typeof clip.sourceAssetId === "string" && !ids.has(clip.sourceAssetId)) {
        issues.push(issue("asset_unknown", `plan.video.clips[${index}].sourceAssetId`, `No asset is registered for source '${clip.sourceAssetId}'.`));
      }
      if (isRecord(clip) && typeof clip.sourceAssetId === "string") usedSourceIds.add(clip.sourceAssetId);
    });
    if (value.schemaVersion === 2) {
      ids.forEach((id) => {
        if (!usedSourceIds.has(id)) issues.push(issue("asset_unused", "assets", `Source asset '${id}' is not used by any clip.`));
      });
    }
    const audio = plan && isRecord(plan.audio) && Array.isArray(plan.audio.sourceClips) ? plan.audio.sourceClips : [];
    audio.forEach((clip, index) => {
      if (isRecord(clip) && typeof clip.segmentId === "string" && !videoSegmentIds.has(clip.segmentId)) {
        issues.push(issue("segment_unknown", `plan.audio.sourceClips[${index}].segmentId`, `No video clip is registered for segment '${clip.segmentId}'.`));
      }
    });
    const v2Audio = plan && isRecord(plan.audio) ? plan.audio : undefined;
    if (v2Audio && typeof v2Audio.musicAssetId === "string" && !ids.has(v2Audio.musicAssetId)) {
      issues.push(issue("asset_unknown", "plan.audio.musicAssetId", `No asset is registered for music '${v2Audio.musicAssetId}'.`));
    }
  }
  return issues;
}
export async function validateAssetFiles(request: RenderRequest): Promise<ValidationIssue[]> {
  const issues: ValidationIssue[] = [];
  const paths = request.schemaVersion === 2
    ? request.assets.flatMap((asset, index) => [
      { path: asset.videoPath, name: asset.sourceAssetId, issuePath: `assets[${index}].videoPath` },
      ...(asset.audioPath ? [{ path: asset.audioPath, name: asset.sourceAssetId, issuePath: `assets[${index}].audioPath` }] : []),
    ])
    : request.assets.map((asset, index) => ({ path: asset.path, name: asset.id, issuePath: `assets[${index}].path` }));
  for (const item of paths) {
    try {
      await access(resolve(item.path));
    } catch {
      issues.push(issue("asset_missing", item.issuePath, `Asset does not exist: ${item.name}.`));
    }
  }
  return issues;
}
