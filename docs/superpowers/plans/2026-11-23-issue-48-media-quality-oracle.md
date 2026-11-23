# Issue #48 Media Quality Oracle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add deterministic project-authored media fixtures and reusable acceptance measurements that detect incorrect montage decisions, not only invalid MP4 files.

**Architecture:** Keep production contracts unchanged. Generate an immutable, versioned oracle beside synthetic acceptance media, then consume it through test-only measurement helpers and public process seams. Build the checks from narrow unit tests to render-host integration and finally the Telegram-to-delivery acceptance flow.

**Tech Stack:** PowerShell, FFmpeg/FFprobe, C#/.NET 10 integration tests, xUnit, Node.js/TypeScript render-host.

**Spec:** `docs/superpowers/specs/2026-11-23-issue-48-media-quality-oracle-design.md`

## Global Constraints

- Do not add user media, full user transcripts, secrets, or generated output to Git.
- Do not change `EditPlan V2`, `RenderRequest`, planner, Telegram flow, or production stage semantics in this issue.
- Keep fixture generation deterministic and create a new revision directory instead of overwriting an existing revision.
- Express timing tolerances in microseconds and frames; express audio tolerances in dB/LUFS where an audio assertion uses them.
- Use public application/process seams; do not test private production methods.
- Run focused tests after each vertical slice before broader verification.

---

### Task 1: Versioned Oracle and Fixture Matrix

**Files:**
- Create: `contracts/media-oracle.v1.schema.json`
- Modify: `scripts/Prepare-AcceptanceMedia.ps1`
- Modify: `docs/acceptance-media.md`
- Modify: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/AcceptanceMediaTests.cs`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/MediaOracleTests.cs`

**Interfaces:**
- Produces `media-oracle.v1.json` with fixture metadata, markers, audio windows, safe regions, and tolerance values.
- Produces fixture IDs for portrait/landscape, 24/30/60 FPS, speech/pause/repeat, no-speech, similar takes, and distinct tones.
- Tests consume the oracle by deserializing it as JSON and checking hashes and declared metadata.

- [ ] **Step 1: Write the failing oracle schema test**

Add a test that loads the generated oracle path from `ACCEPTANCE_MEDIA_ROOT`, requires `schemaVersion == 1`, and requires at least one visual marker, two audio tone windows, one pause window, one repeat group, portrait and landscape entries, and 24/30/60 FPS entries.

- [ ] **Step 2: Run the focused test to verify it fails**

Run:

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter FullyQualifiedName~MediaOracleTests
```

Expected: FAIL because the versioned oracle and required fixture entries do not exist.

- [ ] **Step 3: Add the JSON Schema and deterministic generator output**

Define required fields for fixture ID, relative path, SHA-256, size, orientation, FPS, markers, tone windows, source/output intervals, and tolerances. Extend `Prepare-AcceptanceMedia.ps1` with fixed FFmpeg filter graphs and explicit output names. Write the oracle only after files are generated and hashes/metadata are known.

- [ ] **Step 4: Add hash and metadata verification**

Make `MediaOracleTests` verify every oracle file exists, its byte size and SHA-256 match, its declared dimensions/FPS match FFprobe, and no relative path escapes the fixture root.

- [ ] **Step 5: Run the focused tests to verify they pass**

Run the focused integration test with the documented `ACCEPTANCE_MEDIA_ROOT` environment variable. Expected: all oracle and fixture checks PASS; without the variable tests remain skipped using the existing repository convention.

- [ ] **Step 6: Update fixture documentation**

Document the fixture matrix, oracle revisioning, marker semantics, tolerances and the prohibition on user material in `docs/acceptance-media.md`.

### Task 2: Reusable Frame, Audio, and Metadata Measurements

**Files:**
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/MediaOracleModels.cs`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/FrameMeasurement.cs`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/AudioMeasurement.cs`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/MediaMetadataAssertions.cs`
- Modify: `tests/VideoFactory.Bot.Tests/Rendering/RenderHostProcessAdapterIntegrationTests.cs`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/MediaMeasurementTests.cs`

**Interfaces:**
- `FrameMeasurement.ExtractAsync(IMediaProcessAdapter, string, TimeSpan, CancellationToken)` returns RGB samples for a requested timestamp.
- `FrameMeasurement.AssertMarker(...)` compares expected color/region with a named tolerance.
- `AudioMeasurement.MeasureToneAsync(...)` returns frequency, RMS and peak for a requested interval.
- `MediaMetadataAssertions.AssertMatches(...)` compares typed probe metadata with oracle metadata.

- [ ] **Step 1: Write failing unit/integration tests for marker and tone measurements**

Move the existing red/blue frame and 440/880 Hz assumptions into tests that consume marker and tone records from `media-oracle.v1.json`. Add independent assertions for frequency tolerance, interval containment, and a black-frame sample.

- [ ] **Step 2: Run focused tests to verify they fail**

Run:

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter FullyQualifiedName~MediaMeasurementTests
```

Expected: FAIL because the reusable measurement APIs are not present.

- [ ] **Step 3: Implement measurements through the existing process runner seam**

Extract frames with FFmpeg to a bounded RGB output, measure marker pixels over a region rather than one fragile pixel, and measure tone windows with FFmpeg PCM output and a deterministic zero-crossing/frequency calculation. Keep process arguments as separate arguments and preserve cancellation.

- [ ] **Step 4: Implement typed metadata assertions**

Compare container, dimensions, FPS, duration, video/audio codecs, sample rate, channel layout and pixel format. Report the fixture ID, field name, expected value, actual value and tolerance in assertion failures.

- [ ] **Step 5: Run focused tests to verify they pass**

Run the measurement test class and the existing render-host integration test. Expected: PASS, with no private helper duplication remaining for frame/audio extraction.

### Task 3: Render Quality Assertions

**Files:**
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/MontageQualityAssertions.cs`
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/MontageQualityAssertionTests.cs`
- Modify: `tests/VideoFactory.Bot.Tests/Rendering/RenderHostProcessAdapterIntegrationTests.cs`
- Modify: `tests/VideoFactory.Bot.Tests/Acceptance/TelegramToDeliveryAcceptanceTests.cs`

**Interfaces:**
- `MontageQualityAssertions.AssertSourceMarkerAtOutputFrame(...)` validates source-to-output content mapping.
- `MontageQualityAssertions.AssertClipOrder(...)` validates multiple visual and audio markers.
- `MontageQualityAssertions.AssertNoBlackFrames(...)` samples required transition frames.
- `MontageQualityAssertions.AssertCaptionWindow(...)` checks rendered caption visibility against output-frame tolerances.
- `MontageQualityAssertions.AssertCropSafeRegion(...)` checks required marker retention inside the declared safe region.

- [ ] **Step 1: Add failing assertions for wrong trim, order, audio leakage, black frame, caption timing, and crop**

Build test requests from the existing render fixture and deliberately alter one source range, clip order, audio range, caption range, and crop focal point per test. Each mutation must fail for a content observation rather than only a JSON validation error.

- [ ] **Step 2: Run the focused quality tests to verify they fail for the intended reason**

Run:

```powershell
dotnet test .\tests\VideoFactory.Bot.Tests\VideoFactory.Bot.Tests.csproj --filter FullyQualifiedName~MontageQualityAssertionTests
```

Expected: FAIL with marker, tone, black-frame, caption, or safe-region diagnostics, not a generic process failure.

- [ ] **Step 3: Implement the assertion layer**

Compose the reusable frame/audio/metadata measurements, map source microseconds to expected output frames using the oracle, and keep objective assertions independent of analysis scores. Treat a missing marker, unexpected tone, black frame, late caption, or lost safe-region marker as a permanent test failure.

- [ ] **Step 4: Integrate assertions into the successful acceptance flow**

After the verified MP4 is produced and before delivery is asserted, load the oracle and verify marker order, source trim, audio splice windows, black-frame absence, captions and crop. Keep the corrupt-input scenario unchanged and verify it still sends no file.

- [ ] **Step 5: Run the focused tests to verify they pass**

Run both the quality assertion tests and the two existing Telegram delivery acceptance scenarios with acceptance media enabled. Expected: all pass; the negative MP4 scenario remains undelivered.

### Task 4: Deterministic Repeated Observation and Documentation

**Files:**
- Create: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Media/DeterministicMediaObservationTests.cs`
- Modify: `docs/mvp-acceptance-report.md`
- Modify: `README.md`

**Interfaces:**
- `DeterministicMediaObservationTests` runs the same fixture observation twice and compares oracle-relevant observations.
- The comparison excludes creation timestamps and documents any codec/platform-dependent byte differences.

- [ ] **Step 1: Write the failing repeated-observation test**

Run two observation passes over the same generated revision and compare marker classifications, tone measurements, metadata, duration, and quality assertion results.

- [ ] **Step 2: Run the focused test to verify it fails if observations differ**

Run:

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter FullyQualifiedName~DeterministicMediaObservationTests
```

Expected: FAIL until observation serialization and comparison are implemented.

- [ ] **Step 3: Implement canonical observation comparison**

Serialize only stable values in invariant culture, compare within the oracle tolerances, and include fixture ID and field path in failures. Do not compare generated timestamps or undocumented encoder bytes.

- [ ] **Step 4: Update acceptance documentation and report**

Document exact commands, required tools/environment variables, objective-vs-subjective boundary, fixture matrix, and the new quality checks. Add measured results only from an actual local run.

- [ ] **Step 5: Run the complete verification set**

Run:

```powershell
npm run typecheck --prefix .\render-host
npm run build --prefix .\render-host
npm test --prefix .\render-host
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj
dotnet test .\tests\VideoFactory.Bot.Tests\VideoFactory.Bot.Tests.csproj
git diff --check
```

Expected: all available tests pass; tests requiring absent local media/tools are explicitly reported as skipped, not described as passed.
