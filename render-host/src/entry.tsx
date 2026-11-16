import React from "react";
import { Composition, registerRoot } from "remotion";
import { DynamicComposition } from "./templates/DynamicComposition.js";
import type { RenderRequest } from "./contracts.js";

export const RemotionRoot: React.FC = () => (
  <Composition
    id="dynamic"
    component={DynamicComposition as unknown as React.ComponentType<Record<string, unknown>>}
    durationInFrames={1}
    fps={30}
    width={1080}
    height={1920}
    defaultProps={{} as RenderRequest}
  />
);

registerRoot(RemotionRoot);
