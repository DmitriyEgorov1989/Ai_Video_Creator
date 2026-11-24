# Issue 49 Reels Directors Implementation Plan

> **For agentic workers:** Implement one red-green slice at a time. Do not commit until the user explicitly authorizes Git operations.

**Goal:** Add deterministic Caption, Visual, Smart Reframe, and Audio directors plus Instagram Reels profiles and execute their V2 decisions in verified media.

**Architecture:** C# editing skills own decisions, versioned contracts persist them, and TypeScript/Remotion plus FFmpeg execute only validated directives. V1 remains a separate compatibility path; V2 is no longer converted to V1 before rendering.

**Tech Stack:** .NET 10, C#, xUnit, JSON Schema, TypeScript, React, Remotion, FFmpeg/FFprobe.

**Spec:** `docs/superpowers/specs/2026-11-23-issue-49-reels-directors-design.md`

## Global Constraints

- Add no new dependencies.
- Keep C# as the orchestrator and owner of business decisions.
- Preserve V1 rendering and existing V2 meaning.
- Use only known asset IDs and closed operations.
- Verify rendered media with FFprobe, full decode, frame samples, and audio measurements.

### Task 1: Lossless V2 focal crop

**Files:**
- Modify: `render-host/src/contracts.ts`
- Modify: `render-host/src/validate.ts`
- Modify: `render-host/src/templates/DynamicComposition.tsx`
- Test: `tests/VideoFactory.Bot.Tests/Rendering/RenderHostProcessAdapterIntegrationTests.cs`
- Test: `render-host/tests/validation.test.ts`

**Interface:** `validateRenderRequest(value)` validates V1/V2 and `DynamicComposition` receives a versioned plan without lossy conversion.

- [ ] Add a render test whose V2 `focalX` is required to retain the oracle marker.
- [ ] Run the focused test and confirm marker failure under center crop.
- [ ] Render V2 crop coordinates directly while retaining the V1 branch.
- [ ] Run TypeScript tests and focused integration test to green.

### Task 2: Caption Director

**Files:**
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/CaptionDirectorSkill.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/EditingContracts.cs`
- Modify: `contracts/edit-plan.v2.schema.json`
- Modify: `render-host/src/contracts.ts`
- Modify: `render-host/src/templates/DynamicComposition.tsx`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Editing/CaptionDirectorSkillTests.cs`

**Interface:** `CaptionDirectorSkill : IEditingSkill` returns typed caption groups derived from analysis words and prior timeline decisions.

- [ ] Add a failing Cyrillic grouping test with independent expected groups and output frames.
- [ ] Implement mapping, two-line grouping, safe-zone, highlight, and animation directives.
- [ ] Add schema/runtime validation and render the groups.
- [ ] Add frame assertions for before/visible/after windows and safe-zone bounds.

### Task 3: Visual Director

**Files:**
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/VisualDirectorSkill.cs`
- Modify: `contracts/edit-plan.v2.schema.json`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Domain/Validation/EditPlanContractDispatcher.cs`
- Modify: `render-host/src/contracts.ts`
- Modify: `render-host/src/templates/DynamicComposition.tsx`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Editing/VisualDirectorSkillTests.cs`

**Interface:** `VisualDirectorSkill : IEditingSkill` emits effects with ID, output range, closed type/parameters, and `sourceSkillId`.

- [ ] Add a failing deterministic-effect test.
- [ ] Implement the minimum three closed effect decisions.
- [ ] Add failing tests for dangling provenance and overlapping incompatible effects.
- [ ] Implement semantic validation and Remotion execution.

### Task 4: Smart Reframe

**Files:**
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Analysis/AnalysisFeatures.cs`
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/SmartReframeSkill.cs`
- Modify: `contracts/analysis-result.schema.json`
- Modify: `contracts/edit-plan.v2.schema.json`
- Modify: `render-host/src/contracts.ts`
- Modify: `render-host/src/templates/DynamicComposition.tsx`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Editing/SmartReframeSkillTests.cs`

**Interface:** `SmartReframeSkill : IEditingSkill` consumes optional normalized focal observations and returns crop keyframes or an explicit fallback.

- [ ] Add failing single-subject, multi-subject, movement-bound, and no-observation tests one at a time.
- [ ] Implement deterministic weighted focal points and bounded interpolation.
- [ ] Validate coordinates, monotonic frames, and zoom bounds.
- [ ] Verify keyframes and fallback using synthetic crop fixtures.

### Task 5: Audio Director

**Files:**
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/AudioDirectorSkill.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Media/FfmpegAudioStemBuilder.cs`
- Modify: `contracts/edit-plan.v2.schema.json`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Editing/AudioDirectorSkillTests.cs`
- Modify: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/MediaMeasurements.cs`

**Interface:** `AudioDirectorSkill : IEditingSkill` returns loudness, true peak, fades, optional denoise, and conditional ducking controls.

- [ ] Add a failing default speech policy test.
- [ ] Implement typed audio policy and FFmpeg filter execution.
- [ ] Add a failing ducking-without-music validation test and make it pass.
- [ ] Measure final loudness, peak, fades, and synchronization on the fixture.

### Task 6: Reels profile and styles

**Files:**
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/ReelsProfileCatalog.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Planning/EditPlanPlanner.cs`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Editing/ReelsProfileCatalogTests.cs`

**Interface:** `ReelsProfileCatalog.Get(style)` returns immutable parameters for the same skill IDs; `auto` returns and records a deterministic resolved style.

- [ ] Add failing literal assertions for 1080x1920, 30 FPS, H.264/AAC, and 30-second target policy.
- [ ] Implement the catalog and semantic-boundary selection in 27-33 seconds.
- [ ] Add one deterministic distinction test for each style.
- [ ] Wire the resolved parameters into the existing skill engine configuration.

### Task 7: Contract parity and acceptance

**Files:**
- Modify: `contracts/fixtures/`
- Modify: `render-host/tests/validation.test.ts`
- Modify: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Validation/EditPlanContractDispatcherTests.cs`
- Modify: `tests/VideoFactory.Bot.Tests/Rendering/RenderHostProcessAdapterIntegrationTests.cs`
- Modify: `docs/mvp-acceptance-report.md`

**Interface:** Shared positive/negative V2 fixtures produce equivalent C# and TypeScript validation outcomes.

- [ ] Add compatible V1, legacy V2, enriched V2, and invalid enriched V2 fixtures.
- [ ] Run C#/TypeScript parity tests.
- [ ] Render every style and assert deterministic parameter differences plus observable output.
- [ ] Verify MP4 existence, streams, duration, H.264/AAC, dimensions, FPS, full decode, captions, crop, effects, and audio.
- [ ] Run the full solution and render-host checks, then `git diff --check`.
