export type CropPreset = "fit" | "centerCrop";
export type CaptionPreset = "none" | "minimal" | "emphasis";
export type ColorTheme = "light" | "dark";

export interface OutputRange {
  startFrame: number;
  endFrame: number;
}

export interface VideoClip {
  segmentId: string;
  mapping: Record<string, unknown>;
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

export interface RenderAsset {
  id: string;
  path: string;
}

export interface RenderRequest {
  schemaVersion: 1;
  jobId: string;
  variantId: string;
  plan: EditPlan;
  assets: RenderAsset[];
  outputPath: string;
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
