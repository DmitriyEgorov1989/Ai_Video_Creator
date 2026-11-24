import React from "react";
import { AbsoluteFill, Audio, interpolate, OffthreadVideo, Sequence, useCurrentFrame, useVideoConfig } from "remotion";
import type { Caption, EditPlan, EditPlanV2, OutputRange, RenderRequest, SourceMapping, SourceTimeRange } from "../contracts.js";

type V1Clip = EditPlan["video"]["clips"][number];
type V2Clip = EditPlanV2["video"]["clips"][number];
type V2CaptionWord = EditPlanV2["captions"]["words"][number];
type V2CaptionGroup = NonNullable<EditPlanV2["captions"]["groups"]>[number];

function isV2(plan: EditPlan | EditPlanV2): plan is EditPlanV2 {
  return plan.schemaVersion === 2;
}

function isCaptionGroup(caption: Caption | V2CaptionWord | V2CaptionGroup): caption is V2CaptionGroup {
  return "words" in caption;
}

function clipAsset(clip: V1Clip | V2Clip, request: RenderRequest): { path: string; frameRate: number } | undefined {
  const assetId = "mapping" in clip ? clip.mapping.masterAssetId : clip.sourceAssetId;
  const asset = request.assets.find((item) => item.id === assetId);
  if (!asset) return undefined;
  const frameRate = asset.frameRate;
  return {
    path: asset.path,
    frameRate: frameRate ? frameRate.numerator / frameRate.denominator : 30,
  };
}

function clipSource(clip: V1Clip | V2Clip): SourceTimeRange {
  return "mapping" in clip ? clip.mapping.master : clip.master;
}

function activeEffect(plan: EditPlanV2, type: EditPlanV2["effects"][number]["type"], frame: number) {
  return plan.effects.find((effect) => effect.type === type
    && effect.output
    && frame >= effect.output.startFrame
    && frame < effect.output.endFrame);
}

function clipStyle(clip: V1Clip | V2Clip, width: number, height: number, frame: number, plan: EditPlan | EditPlanV2): React.CSSProperties {
  if ("mapping" in clip) {
    return { width, height, objectFit: clip.crop === "fit" ? "contain" : "cover" };
  }

  const crop = resolveCrop(clip, frame);
  const zoomEffect = isV2(plan) ? activeEffect(plan, "dynamicZoom", frame) : undefined;
  const effectProgress = zoomEffect?.output
    ? (frame - zoomEffect.output.startFrame) / Math.max(1, zoomEffect.output.endFrame - zoomEffect.output.startFrame - 1)
    : 0;
  const zoom = crop.zoom * (1 + (zoomEffect?.parameters.amount ?? 0) * effectProgress);
  return {
    width,
    height,
    objectFit: clip.crop.preset === "fit" ? "contain" : "cover",
    objectPosition: `${crop.focalX * 100}% ${crop.focalY * 100}%`,
    transform: zoom === 1 ? undefined : `scale(${zoom})`,
    transformOrigin: `${crop.focalX * 100}% ${crop.focalY * 100}%`,
  };
}

function resolveCrop(clip: V2Clip, frame: number): { focalX: number; focalY: number; zoom: number } {
  const keyframes = clip.crop.keyframes;
  if (!keyframes?.length) return clip.crop;
  const rightIndex = keyframes.findIndex((keyframe) => keyframe.outputFrame >= frame);
  if (rightIndex <= 0) return rightIndex === 0 ? keyframes[0] : keyframes[keyframes.length - 1];
  const left = keyframes[rightIndex - 1];
  const right = keyframes[rightIndex];
  return {
    focalX: interpolate(frame, [left.outputFrame, right.outputFrame], [left.focalX, right.focalX]),
    focalY: interpolate(frame, [left.outputFrame, right.outputFrame], [left.focalY, right.focalY]),
    zoom: interpolate(frame, [left.outputFrame, right.outputFrame], [left.zoom, right.zoom]),
  };
}

function sourceStartFrame(mapping: SourceMapping, sourceFps: number): number {
  return Math.max(0, Math.round(mapping.master.startMicroseconds * sourceFps / 1_000_000));
}

export const DynamicComposition: React.FC<RenderRequest> = ({ plan, assets }) => {
  const frame = useCurrentFrame();
  const { width, height, fps } = useVideoConfig();
  const clip = plan.video.clips.find((item) => frame >= item.output.startFrame && frame < item.output.endFrame) as V1Clip | V2Clip | undefined;
  const source = clip ? clipAsset(clip, { plan, assets } as RenderRequest) : undefined;
  const audio = assets.find((asset) => asset.id === "__audio_master")?.path;
  const music = isV2(plan) && plan.audio.musicAssetId
    ? assets.find((asset) => asset.id === plan.audio.musicAssetId)?.path
    : undefined;
  const captionItems = isV2(plan) && plan.captions.groups?.length
    ? plan.captions.groups
    : isV2(plan) ? plan.captions.words : plan.captions.items;
  const caption = captionItems
    .find((item) => frame >= item.output.startFrame && frame < item.output.endFrame);
  const captionGroup = caption && isCaptionGroup(caption) ? caption : undefined;
  const textHighlight = isV2(plan) ? activeEffect(plan, "textHighlight", frame) : undefined;
  const transition = isV2(plan) ? activeEffect(plan, "visualTransition", frame) : undefined;
  const lightTheme = !isV2(plan) && plan.templateProps.theme === "light";
  const theme = lightTheme ? "#f5f1e8" : "#111318";
  const foreground = lightTheme ? "#141414" : "#ffffff";

  return (
    <AbsoluteFill style={{ backgroundColor: theme, color: foreground, fontFamily: "Arial, sans-serif" }}>
      {source && clip ? (
        <Sequence from={clip.output.startFrame} durationInFrames={clip.output.endFrame - clip.output.startFrame}>
          <OffthreadVideo
            src={source.path}
            muted
            startFrom={Math.max(0, Math.round(clipSource(clip).startMicroseconds * source.frameRate / 1_000_000))}
            style={clipStyle(clip, width, height, frame, plan)}
          />
        </Sequence>
      ) : null}
      {transition?.output ? (
        <AbsoluteFill style={{
          backgroundColor: "#000000",
          opacity: transitionOpacity(frame, transition.output, transition.parameters.amount ?? 0.2),
        }} />
      ) : null}
      {audio && !isV2(plan) ? plan.audio.sourceClips.map((audioClip) => {
        const videoClip = plan.video.clips.find((item) => item.segmentId === audioClip.segmentId);
        if (!videoClip) return null;
        return (
          <Sequence key={`${audioClip.segmentId}-${audioClip.output.startFrame}`} from={audioClip.output.startFrame} durationInFrames={audioClip.output.endFrame - audioClip.output.startFrame}>
            <Audio src={audio} startFrom={sourceStartFrame(videoClip.mapping, fps)} volume={audioClip.gain} />
          </Sequence>
        );
      }) : null}
      {isV2(plan) ? plan.video.clips.map((audioClip, index) => {
        const asset = assets.find((item) => item.id === audioClip.sourceAssetId);
        const audioSource = audio ?? asset?.path;
        if (!audioSource) return null;
        const duration = audioClip.output.endFrame - audioClip.output.startFrame;
        return (
          <Sequence key={`v2-audio-${index}`} from={audioClip.output.startFrame} durationInFrames={duration}>
            <Audio
              src={audioSource}
              startFrom={Math.max(0, Math.round(audioClip.master.startMicroseconds * fps / 1_000_000))}
              volume={(localFrame) => plan.audio.sourceGain * fadeVolume(
                localFrame,
                duration,
                plan.audio.fadeInFrames ?? 0,
                plan.audio.fadeOutFrames ?? 0,
              )}
            />
          </Sequence>
        );
      }) : null}
      {isV2(plan) && music ? (
        <Audio
          src={music}
          volume={(plan.audio.musicGain ?? 1) * (1 - (plan.audio.ducking ?? 0))}
        />
      ) : null}
      {!isV2(plan) && plan.templateProps.title ? <div style={{ position: "absolute", top: 100, left: 70, right: 70, fontSize: 58, fontWeight: 700 }}>{plan.templateProps.title}</div> : null}
      {caption && (isV2(plan) || plan.templateProps.captions !== "none") ? (
        <Sequence from={caption.output.startFrame} durationInFrames={caption.output.endFrame - caption.output.startFrame}>
          <div style={{
            position: "absolute",
            bottom: captionGroup ? 220 : 180,
            left: 70,
            right: 70,
            padding: 24,
            textAlign: "center",
            fontSize: isV2(plan) || plan.templateProps.captions === "emphasis" ? 58 : 44,
            fontWeight: 700,
            backgroundColor: "rgba(0,0,0,0.72)",
            borderRadius: 18,
            overflow: "hidden",
            display: "-webkit-box",
            WebkitBoxOrient: "vertical",
            WebkitLineClamp: captionGroup?.maxLines ?? 2,
            opacity: captionGroup?.animation === "fade"
              ? captionOpacity(frame, caption.output)
              : 1,
          }}>
            {captionGroup
              ? captionGroup.words.map((word, index) => (
                <React.Fragment key={`${word.output.startFrame}-${index}`}>
                  {index > 0 ? " " : null}
                  <span style={{ color: textHighlight && captionGroup.highlight === "activeWord" && frame >= word.output.startFrame && frame < word.output.endFrame ? "#ffe45c" : foreground }}>
                    {word.text}
                  </span>
                </React.Fragment>
              ))
              : caption.text}
          </div>
        </Sequence>
      ) : null}
    </AbsoluteFill>
  );
};

function captionOpacity(frame: number, output: OutputRange): number {
  const fadeFrames = Math.min(3, Math.floor((output.endFrame - output.startFrame) / 2));
  if (fadeFrames === 0) return 1;
  return interpolate(
    frame,
    [output.startFrame, output.startFrame + fadeFrames, output.endFrame - fadeFrames, output.endFrame],
    [0, 1, 1, 0],
    { extrapolateLeft: "clamp", extrapolateRight: "clamp" },
  );
}

function transitionOpacity(frame: number, output: OutputRange, amount: number): number {
  const middle = (output.startFrame + output.endFrame) / 2;
  return interpolate(
    frame,
    [output.startFrame, middle, output.endFrame],
    [0, amount, 0],
    { extrapolateLeft: "clamp", extrapolateRight: "clamp" },
  );
}

function fadeVolume(frame: number, duration: number, fadeInFrames: number, fadeOutFrames: number): number {
  const fadeIn = Math.min(fadeInFrames, Math.floor(duration / 2));
  const fadeOut = Math.min(fadeOutFrames, Math.floor(duration / 2));
  const inVolume = fadeIn === 0 ? 1 : Math.min(1, frame / fadeIn);
  const outVolume = fadeOut === 0 ? 1 : Math.min(1, (duration - frame) / fadeOut);
  return Math.max(0, Math.min(inVolume, outVolume));
}
