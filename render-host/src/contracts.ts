export type CropPreset = "fit" | "centerCrop";
export type CaptionPreset = "none" | "minimal" | "emphasis";
export type ColorTheme = "light" | "dark";

export interface OutputRange {
  startFrame: number;
  endFrame: number;
}

export interface SourceTimeRange {
  startMicroseconds: number;
  endMicroseconds: number;
}

export interface SourceMapping {
  originalAssetId: string;
  original: SourceTimeRange;
  masterAssetId: string;
  master: SourceTimeRange;
}

export interface VideoClip {
  segmentId: string;
  mapping: SourceMapping;
  output: OutputRange;
  crop: CropPreset;
}

export interface Caption {
  text: string;
  output: OutputRange;
}

export interface EditPlan {
  schemaVersion: 1;
  jobId: string;
  variantId: string;
  revisionId: string;
  analysisRevisionId: string;
  seed: number;
  output: {
    width: number;
    height: number;
    frameRate: { numerator: number; denominator: number };
    durationFrames: number;
  };
  template: "dynamic";
  templateProps: {
    captions: CaptionPreset;
    theme: ColorTheme;
    title?: string | null;
  };
  video: { clips: VideoClip[] };
  audio: {
    sourceClips: Array<{ segmentId: string; output: OutputRange; gain: number }>;
    musicClips: [];
  };
  captions: { items: Caption[] };
  overlays: { items: Array<{ type: "text"; text: string; output: OutputRange }> };
}

export interface EditPlanV2 {
  schemaVersion: 2;
  jobId: string;
  variantId: string;
  revisionId: string;
  analysisRevisionId: string;
  output: { width: number; height: number; frameRate: { numerator: number; denominator: number }; durationFrames: number };
  source: { originalAssetId: string; masterAssetId: string };
  video: { clips: Array<{ sourceAssetId: string; source: SourceTimeRange; master: SourceTimeRange; output: OutputRange; crop: { preset: "fit" | "fill"; focalX: number; focalY: number; zoom: number }; effects: string[] }> };
  effects: Array<{ type: "speed" | "transition" | "blur"; parameters: { rate?: number; kind?: "cut" | "crossfade" | "dipToBlack"; durationFrames?: number; amount?: number } }>;
  captions: { words: Array<{ text: string; output: OutputRange }> };
  audio: { sourceGain: number; musicAssetId?: string | null; musicGain?: number; ducking?: number };
  skills: Array<{ id: string; version: string; status: "applied" | "skipped" | "rejected" }>;
}

export interface RenderAsset {
  id: string;
  path: string;
  frameRate?: { numerator: number; denominator: number } | null;
}

export interface RenderRequest {
  schemaVersion: 1;
  jobId: string;
  variantId: string;
  plan: EditPlan;
  assets: RenderAsset[];
  outputPath: string;
  encoding: {
    videoBitrateBitsPerSecond: number;
    audioBitrateBitsPerSecond: number;
  };
}

export interface ProgressEvent {
  type: "progress";
  stage: string;
  percent: number;
}

export interface CompletedEvent {
  type: "completed";
  outputPath: string;
  durationFrames: number;
}

export interface FailedEvent {
  type: "failed";
  code: string;
  message: string;
}

export type RenderEvent = ProgressEvent | CompletedEvent | FailedEvent;
