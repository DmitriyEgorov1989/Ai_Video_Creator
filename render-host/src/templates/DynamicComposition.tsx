import React from "react";
import { AbsoluteFill, OffthreadVideo, Sequence, useCurrentFrame, useVideoConfig } from "remotion";
import type { RenderRequest, VideoClip } from "../contracts.js";

function clipAsset(clip: VideoClip, request: RenderRequest): string | undefined {
  return request.assets.find((asset) => asset.id === clip.segmentId)?.path;
}

export const DynamicComposition: React.FC<RenderRequest> = ({ plan, assets }) => {
  const frame = useCurrentFrame();
  const { width, height } = useVideoConfig();
  const clip = plan.video.clips.find((item) => frame >= item.output.startFrame && frame < item.output.endFrame);
  const source = clip ? clipAsset(clip, { plan, assets } as RenderRequest) : undefined;
  const caption = plan.captions.items.find((item) => frame >= item.output.startFrame && frame < item.output.endFrame);
  const theme = plan.templateProps.theme === "light" ? "#f5f1e8" : "#111318";
  const foreground = plan.templateProps.theme === "light" ? "#141414" : "#ffffff";

  return (
    <AbsoluteFill style={{ backgroundColor: theme, color: foreground, fontFamily: "Arial, sans-serif" }}>
      {source && clip ? (
        <OffthreadVideo
          src={source}
          startFrom={Math.max(0, Math.floor((frame - clip.output.startFrame) * 30 / 30))}
          style={{ width, height, objectFit: clip.crop === "fit" ? "contain" : "cover" }}
        />
      ) : null}
      {plan.templateProps.title ? <div style={{ position: "absolute", top: 100, left: 70, right: 70, fontSize: 58, fontWeight: 700 }}>{plan.templateProps.title}</div> : null}
      {caption && plan.templateProps.captions !== "none" ? (
        <Sequence from={caption.output.startFrame} durationInFrames={caption.output.endFrame - caption.output.startFrame}>
          <div style={{ position: "absolute", bottom: 180, left: 50, right: 50, padding: 24, textAlign: "center", fontSize: plan.templateProps.captions === "emphasis" ? 58 : 44, fontWeight: 700, backgroundColor: "rgba(0,0,0,0.65)", borderRadius: 18 }}>{caption.text}</div>
        </Sequence>
      ) : null}
    </AbsoluteFill>
  );
};
