import React from "react";
import { AbsoluteFill, Audio, OffthreadVideo, Sequence, useCurrentFrame, useVideoConfig } from "remotion";
import type { RenderRequest, SourceMapping, VideoClip } from "../contracts.js";

function clipAsset(clip: VideoClip, request: RenderRequest): { path: string; frameRate: number } | undefined {
  const asset = request.assets.find((item) => item.id === clip.mapping.masterAssetId);
  if (!asset) return undefined;
  const frameRate = asset.frameRate;
  return {
    path: asset.path,
    frameRate: frameRate ? frameRate.numerator / frameRate.denominator : 30,
  };
}

function sourceStartFrame(mapping: SourceMapping, sourceFps: number): number {
  return Math.max(0, Math.round(mapping.master.startMicroseconds * sourceFps / 1_000_000));
}

export const DynamicComposition: React.FC<RenderRequest> = ({ plan, assets }) => {
  const frame = useCurrentFrame();
  const { width, height, fps } = useVideoConfig();
  const clip = plan.video.clips.find((item) => frame >= item.output.startFrame && frame < item.output.endFrame);
  const source = clip ? clipAsset(clip, { plan, assets } as RenderRequest) : undefined;
  const audio = assets.find((asset) => asset.id === "__audio_master")?.path;
  const caption = plan.captions.items.find((item) => frame >= item.output.startFrame && frame < item.output.endFrame);
  const theme = plan.templateProps.theme === "light" ? "#f5f1e8" : "#111318";
  const foreground = plan.templateProps.theme === "light" ? "#141414" : "#ffffff";

  return (
    <AbsoluteFill style={{ backgroundColor: theme, color: foreground, fontFamily: "Arial, sans-serif" }}>
      {source && clip ? (
        <Sequence from={clip.output.startFrame} durationInFrames={clip.output.endFrame - clip.output.startFrame}>
          <OffthreadVideo
            src={source.path}
            muted
            startFrom={sourceStartFrame(clip.mapping, source.frameRate)}
            style={{ width, height, objectFit: clip.crop === "fit" ? "contain" : "cover" }}
          />
        </Sequence>
      ) : null}
      {audio ? plan.audio.sourceClips.map((audioClip) => {
        const videoClip = plan.video.clips.find((item) => item.segmentId === audioClip.segmentId);
        if (!videoClip) return null;
        return (
          <Sequence key={`${audioClip.segmentId}-${audioClip.output.startFrame}`} from={audioClip.output.startFrame} durationInFrames={audioClip.output.endFrame - audioClip.output.startFrame}>
            <Audio src={audio} startFrom={sourceStartFrame(videoClip.mapping, fps)} volume={audioClip.gain} />
          </Sequence>
        );
      }) : null}
      {plan.templateProps.title ? <div style={{ position: "absolute", top: 100, left: 70, right: 70, fontSize: 58, fontWeight: 700 }}>{plan.templateProps.title}</div> : null}
      {caption && plan.templateProps.captions !== "none" ? (
        <Sequence from={caption.output.startFrame} durationInFrames={caption.output.endFrame - caption.output.startFrame}>
          <div style={{ position: "absolute", bottom: 180, left: 50, right: 50, padding: 24, textAlign: "center", fontSize: plan.templateProps.captions === "emphasis" ? 58 : 44, fontWeight: 700, backgroundColor: "rgba(0,0,0,0.65)", borderRadius: 18 }}>{caption.text}</div>
        </Sequence>
      ) : null}
    </AbsoluteFill>
  );
};
