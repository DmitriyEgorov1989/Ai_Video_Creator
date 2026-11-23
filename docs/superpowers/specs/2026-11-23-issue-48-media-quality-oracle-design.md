# Issue #48: Media Quality Oracle

## Purpose

Issue #48 adds a deterministic, project-authored media oracle so acceptance tests verify montage decisions, not only that an MP4 exists and decodes. The oracle must detect incorrect source trims, clip order, audio splices, black frames, caption timing, and unsafe crops without storing user media or complete user transcripts.

## Scope

This change is limited to acceptance fixtures, test-support measurements, documentation, and acceptance tests. It does not change the production Edit Plan V2 contract, planner behavior, Remotion semantics, or the Telegram flow.

## Design

### Oracle contract

Add a versioned `media-oracle.v1.json` to each generated acceptance-media revision. It will contain:

- fixture and source file identifiers;
- file size, SHA-256, and typed media metadata;
- visual markers with source timestamps and expected output locations;
- audio tone windows and expected frequencies;
- expected speech, pause, and repeat windows;
- crop-safe marker regions;
- tolerances expressed in microseconds, frames, and dB/LUFS where applicable.

The existing generation manifest remains responsible for generation bookkeeping. The oracle is the immutable acceptance expectation consumed by tests.

### Fixtures

Extend the project-authored generator with deterministic synthetic media covering portrait and landscape orientation, 24/30/60 FPS, speech and pauses, repeated speech/visual content, no-speech, two similar takes, and distinct audio tones. Every generated revision is written to a new directory and verified against its hashes.

### Test measurements

Create reusable test-support APIs for frame extraction and marker matching, tone/frequency and audio-window measurement, loudness/peak measurement, and typed FFprobe metadata assertions. These helpers remain in test projects and are accessed through public process/application seams.

### Quality assertions

Add acceptance tests that compare actual rendered media with the oracle for source trim, clip order, audio splice continuity, black-frame absence, caption timing, and crop-safe regions. Objective media observations are separate from analysis heuristics such as motion, sharpness, and confidence.

### Determinism

Run generation and observation checks repeatedly. Compare oracle-relevant observations and hashes; exclude timestamps and explicitly platform-dependent encoding bytes unless the test contract requires them.

## Success criteria

- The generated oracle and all fixture files pass hash and metadata validation.
- A deliberately wrong trim or clip order fails an observable marker assertion.
- Continuous audio from a removed source interval fails an audio-window assertion.
- A black frame, caption timing error, or unsafe crop fails an acceptance assertion.
- A repeated run produces equivalent oracle observations.
- Existing MVP acceptance tests continue to pass.
- No user media or complete user transcripts are added to the repository.

## Verification order

1. Measurement helper unit tests.
2. Fixture generation and oracle/hash tests.
3. Render-host marker and audio tests.
4. End-to-end quality acceptance tests.
5. Full relevant .NET and Node checks, smoke render, and `git diff --check`.
