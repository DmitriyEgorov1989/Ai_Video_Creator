# Issue 49 Reels Directors Design

## Goal

Execute professional but controlled presentation decisions over a correct Edit Plan V2 timeline. Caption, visual, reframe, and audio decisions remain deterministic C# editing skills; Remotion and FFmpeg only execute a validated plan.

## Compatibility

- Existing V1 plans remain readable and renderable.
- Existing V2 fields retain their meaning; new V2 fields are optional where compatibility requires it.
- C#, JSON Schema, and TypeScript accept the same contract fixtures.
- No new font, CV, audio, NuGet, npm, or Python dependency is introduced.
- Missing focal observations produce an explicit safe fallback rather than guessed tracking.

## Data Flow

Analysis provides word timings and optional normalized focal observations. The editing engine runs the existing semantic skills, then Caption Director, Visual Director, Smart Reframe, and Audio Director. A style catalog supplies parameters to the same skills; it does not select a separate pipeline. The resulting V2 plan records the requested and resolved style, applied skill metadata, caption groups, closed effects, crop keyframes, and audio controls. Rendering validates and executes these fields without normalizing V2 into V1.

## Contract

The V2 contract adds:

- an `instagram-reels` profile and requested/resolved style metadata;
- grouped captions with bounded line count, highlight mode, animation, and safe-zone placement;
- effects with stable IDs, output ranges, closed parameters, and originating skill IDs;
- crop keyframes with normalized focal coordinates and zoom plus an explicit fallback;
- audio fades, optional denoise, and music ducking conditional on a registered music asset.

Unknown operations, arbitrary paths, executable code, dangling effect references, incompatible overlapping effects, and ducking without music are validation failures.

## Directors

Caption Director maps analysis words through selected source/master/output ranges, groups them without splitting a word, preserves Unicode text, and limits each group to two rendered lines. Highlight and animation values come from the style configuration.

Visual Director emits only `dynamicZoom`, `textHighlight`, and `visualTransition` decisions. Every effect records `sourceSkillId`; the validator rejects effects not backed by an applied skill or conflicting on the same output interval.

Smart Reframe converts optional focal observations into bounded keyframes and limits movement between adjacent frames. Multiple observations use a weighted bounding region. Missing or low-confidence observations produce the configured `fit` or stable center fallback.

Audio Director selects speech loudness and true-peak targets, short fades, optional FFmpeg denoise, and ducking only when a known music asset exists. Final acceptance measures the rendered output rather than trusting configured filter arguments.

## Profile And Styles

The `instagram-reels` profile fixes 1080x1920, 30 FPS, H.264/AAC, and a target duration of 30 seconds. Planning chooses a complete semantic boundary in 27-33 seconds when available and otherwise uses the nearest complete boundary instead of cutting a word.

The styles `auto`, `expert-clean`, `dynamic-viral`, `cinematic`, and `premium-ads` are immutable parameter sets for the same skills. `auto` deterministically resolves to one concrete style and records that result in the plan.

## Errors And Degradation

Expected missing inputs yield `Skipped` or `Unavailable` editing decisions with stable reason codes. Invalid plans return validation issues and never start rendering. Focal tracking absence is not an error. Missing referenced assets, invalid audio controls, conflicting effects, and unsafe ranges are errors.

## Verification

Tests exercise public seams: editing skills through `IEditingSkill`, contract semantics through the version dispatcher and render validator, and observable media through render-host plus the media oracle. Acceptance includes Cyrillic captions, line bounds, effect provenance/conflicts, crop stability/fallback, measured loudness/peak/sync, 1080x1920 at 30 FPS, H.264/AAC, smoke render, and full decode.
