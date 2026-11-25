# Multi-source Telegram Intake Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: implement this plan task-by-task with TDD and a review checkpoint after every task. The repository does not expose `subagent-driven-development` or `executing-plans`; use the available runtime without installing missing skills. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Один устойчивый Telegram intake принимает 1-5 видео, явно завершается командой `/done` и создаёт один Job, Analysis V3, Edit Plan V3 и проверенный MP4 с корректными video/audio fragments нескольких sources.

**Architecture:** SQLite владеет intake state и ordered Job sources. C# готовит и анализирует каждый source независимо, агрегирует source-scoped Analysis V3, строит один source-aware plan и передаёт Remotion RenderRequest V2 с отдельными video/audio assets. Legacy single-source Job, Analysis V1/V2, Edit Plan V1/V2 и RenderRequest V1 остаются читаемыми.

**Tech Stack:** .NET 10, C#, SQLite, xUnit, Telegram.Bot, TypeScript, React/Remotion, Node test runner, FFmpeg/FFprobe, JSON Schema.

**Spec:** `docs/superpowers/specs/2026-11-24-issue-50-multi-source-telegram-intake-design.md`

## Global Constraints

- C# остаётся единственным оркестратором и владельцем Job state.
- Один Job создаёт ровно один Variant и один итоговый MP4.
- Intake принимает 1-5 отдельных Telegram video messages; albums и `media_group_id` вне scope.
- Aggregate quota: не более 524 288 000 байт и 600 секунд проверенной длительности.
- `/duration` принимает целое значение 5-60 секунд.
- Processing не запускается до успешного `/done`.
- SQLite migration для issue #50 явно разрешена в issue; новые dependencies запрещены.
- V1/V2 contracts и существующие SQLite Job должны оставаться читаемыми.
- Пользовательские строки не попадают в system instructions, shell interpolation и обычные логи.
- Каждый behavioral slice выполняется `red -> green -> refactor` через публичный seam.
- Commit steps являются контрольными точками плана; выполнять их только при действующем разрешении пользователя на Git mutations.

---

## File Structure

### Domain and Application

- `Domain/Jobs/JobRequest.cs`: ordered source IDs и legacy primary-source accessor.
- `Domain/Jobs/ProductionSettings.cs`: доменный requested Reels style.
- `Application/Jobs/CreateJob/CreateJobCommand.cs`: ordered source command.
- `Application/Jobs/CreateJob/IJobCreationStore.cs`: атомарное создание Job с набором sources.
- `Application/TelegramIntake/TelegramIntakeContracts.cs`: async state transitions intake без Telegram SDK types.
- `Application/Analysis/MultiSourceAnalysisContracts.cs`: Analysis V3 source aggregate и legacy adaptation seam.
- `Application/Editing/MultiClipDirectorSkill.cs`: cross-source selection decision.

### Infrastructure and Host

- `Infrastructure/Storage/SqliteMigrationRunner.cs`: migration 11 для Job sources/settings и migration 12 для intake tables.
- `Infrastructure/Storage/SqliteJobWriter.cs`: общий transaction-aware writer для обычного create и intake finalize.
- `Infrastructure/Storage/SqliteJobCreationStore.cs`: transaction для Job, Variant, source links и artifact ownership.
- `Infrastructure/Storage/SqliteTelegramIntakeStore.cs`: reservations, dedupe, commands, finalize receipts и recovery.
- `Infrastructure/Media/PreparationStageExecutor.cs`: per-source Master recovery.
- `Infrastructure/Analysis/AnalysisStageExecutor.cs`: per-source results и Analysis V3 aggregate.
- `Infrastructure/Planning/PlanningStageExecutor.cs`: полный source asset catalog.
- `Infrastructure/Rendering/RenderingStageExecutor.cs`: Edit Plan V3 и RenderRequest V2.
- `VideoFactory.Bot/Telegram/TelegramUpdateHandler.cs`: command UX поверх application intake seam.
- `render-host/src/contracts.ts`, `validate.ts`, `DynamicComposition.tsx`: RenderRequest V2 и source-specific audio.

---

### Task 1: Ordered Job Sources and Requested Style

**Files:**
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Domain/Jobs/JobRequest.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Domain/Jobs/ProductionSettings.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Jobs/CreateJob/CreateJobCommand.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Jobs/CreateJob/CreateJobUseCase.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Jobs/CreateJob/IJobCreationStore.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Jobs/JobRequestTests.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Jobs/CreateJobUseCaseTests.cs`

**Interfaces:**
- Produces: `JobRequest.SourceAssetIds`, `JobRequest.PrimarySourceAssetId`, `RequestedReelsStyle`, ordered `CreateJobCommand.SourceArtifactIds`.
- Compatibility: retain a one-source constructor only where existing external callers require it; all new code uses the ordered collection.

- [ ] **Step 1: Add failing domain tests for source invariants**

```csharp
[Fact]
public void PreservesOneToFiveUniqueSourcesInOrder()
{
    ArtifactId first = ArtifactId.New();
    ArtifactId second = ArtifactId.New();

    JobRequest request = new(
        [first, second],
        "instruction",
        ProductionMode.Fast,
        new ProductionSettings(null, true, false, EditTempo.Dynamic, RequestedReelsStyle.Cinematic));

    Assert.Equal([first, second], request.SourceAssetIds);
    Assert.Equal(first, request.PrimarySourceAssetId);
    Assert.Equal(RequestedReelsStyle.Cinematic, request.Settings.RequestedStyle);
}

[Theory]
[InlineData(0)]
[InlineData(6)]
public void RejectsSourceCountOutsideOneToFive(int count)
{
    ArtifactId[] sources = Enumerable.Range(0, count).Select(_ => ArtifactId.New()).ToArray();
    Assert.Throws<ArgumentOutOfRangeException>(() => new JobRequest(
        sources, "instruction", ProductionMode.Fast, new ProductionSettings(null, true, false)));
}
```

- [ ] **Step 2: Run the narrow tests and confirm RED**

Run:

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.UnitTests\VideoFactory.VideoProduction.UnitTests.csproj --filter "FullyQualifiedName~JobRequestTests"
```

Expected: compile failure because ordered sources and `RequestedReelsStyle` do not exist.

- [ ] **Step 3: Implement the minimal domain contracts**

```csharp
public enum RequestedReelsStyle
{
    Auto,
    ExpertClean,
    DynamicViral,
    Cinematic,
    PremiumAds
}

public sealed record JobRequest
{
    public ImmutableArray<ArtifactId> SourceAssetIds { get; }
    public ArtifactId PrimarySourceAssetId => SourceAssetIds[0];
    // Existing instruction, mode and settings members remain unchanged.
}
```

Validate count `1..5`, non-empty IDs and uniqueness while preserving input order. Add Russian XML documentation to every new public type/member, including null semantics.

- [ ] **Step 4: Add failing CreateJob tests**

Assert that `CreateJobUseCase` passes all sources to the store in order, rejects duplicates as an expected result, and publishes exactly one queue signal only when the store returns `Created`.

- [ ] **Step 5: Update `CreateJobCommand`, use case and store seam**

Use this signature:

```csharp
public sealed record CreateJobCommand(
    IReadOnlyList<ArtifactId> SourceArtifactIds,
    string Instruction,
    ProductionMode Mode,
    ProductionSettings Settings);
```

Do not add persistence compatibility here; Task 2 implements it in SQLite.

- [ ] **Step 6: Run all Job unit tests**

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.UnitTests\VideoFactory.VideoProduction.UnitTests.csproj --filter "FullyQualifiedName~Jobs"
```

Expected: PASS.

- [ ] **Step 7: Commit checkpoint**

```powershell
git add src/Modules/VideoProduction tests/Modules/VideoProduction
git commit -m "feat: model ordered job sources"
```

---

### Task 2: SQLite Job Sources Migration and Recovery

**Files:**
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Storage/SqliteMigrationRunner.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Storage/SqliteJobCreationStore.cs`
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Storage/SqliteJobWriter.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Storage/SqliteJobStateStore.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Storage/SqliteMigrationTests.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Storage/SqliteJobCreationStoreTests.cs`

**Interfaces:**
- Consumes: ordered `JobRequest.SourceAssetIds` from Task 1.
- Produces: migration 11 and authoritative `job_sources` read/write path.

- [ ] **Step 1: Write migration RED tests**

Create a version-10 database through existing migrations, insert a legacy Job, run migration 11, and assert:

```csharp
Assert.Equal(11, await ReadSchemaVersionAsync(connection));
Assert.Equal((legacyJobId, legacySourceId, 0), await ReadOnlyJobSourceAsync(connection));
Assert.False(await HasUniqueIndexAsync(connection, "jobs", "source_artifact_id"));
```

Also assert these constraints reject invalid rows:

- duplicate `(job_id, source_order)`;
- duplicate `(job_id, source_artifact_id)`;
- missing Job or Artifact foreign key.

- [ ] **Step 2: Run migration tests and confirm RED**

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter "FullyQualifiedName~SqliteMigrationTests"
```

Expected: version remains 10 and `job_sources` is absent.

- [ ] **Step 3: Implement migration 11**

Rebuild `jobs` inside one SQLite transaction to remove only the legacy unique constraint while retaining all columns, values, foreign keys and indexes. Add:

```sql
CREATE TABLE job_sources (
    job_id TEXT NOT NULL,
    source_artifact_id TEXT NOT NULL,
    source_order INTEGER NOT NULL CHECK (source_order >= 0),
    created_at TEXT NOT NULL,
    PRIMARY KEY (job_id, source_artifact_id),
    UNIQUE (job_id, source_order),
    FOREIGN KEY (job_id) REFERENCES jobs(job_id) ON DELETE CASCADE,
    FOREIGN KEY (source_artifact_id) REFERENCES artifacts(artifact_id)
);
```

Backfill ordinal `0`, add `edit_tempo` default `Dynamic` and `requested_style` default `Auto`, and preserve migration idempotency.

- [ ] **Step 4: Write store RED tests for ordered creation and restart**

Create a Job with two registered Original artifacts. Reopen the database through a fresh store and assert source order, tempo and requested style are unchanged. Assert one Job, one Variant and two ordered links.

- [ ] **Step 5: Implement transactional Job creation**

Extract an Infrastructure-only `SqliteJobWriter` whose method accepts an open
`SqliteConnection` and `SqliteTransaction`. `SqliteJobCreationStore` starts its
own transaction and delegates to that writer; Task 3 reuses the same writer in
the intake completion transaction. In a single transaction:

1. validate every source artifact exists;
2. insert Job with ordinal-0 source in legacy column;
3. insert one Variant;
4. insert ordered `job_sources` rows;
5. assign all source artifacts to the Job;
6. return the complete `JobCreationRecord`.

Do not use the first source ID as the idempotency key.

- [ ] **Step 6: Run storage tests**

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter "FullyQualifiedName~SqliteMigrationTests|FullyQualifiedName~SqliteJobCreationStoreTests"
```

Expected: PASS, including legacy backfill and fresh-store recovery.

- [ ] **Step 7: Commit checkpoint**

```powershell
git add src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Storage tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Storage
git commit -m "feat: persist ordered job sources"
```

---

### Task 3: Persistent Intake State Machine

**Files:**
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/TelegramIntake/TelegramIntakeContracts.cs`
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Storage/SqliteTelegramIntakeStore.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Storage/SqliteMigrationRunner.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Storage/SqliteTelegramIntakeStoreTests.cs`

**Interfaces:**
- Produces: `ITelegramIntakeStore` with reservation, acceptance, failure, settings, finalize receipt, cancellation and startup recovery transitions.
- Consumes: `IJobCreationStore` from Task 2 during atomic finalize.

- [ ] **Step 1: Define the state-machine seam in a failing integration test**

Use explicit expected-result unions, not exceptions:

```csharp
public interface ITelegramIntakeStore
{
    Task<IntakeReservationResult> ReserveAsync(IntakeReservation request, CancellationToken token);
    Task<IntakeItemResult> AcceptAsync(IntakeAcceptance request, CancellationToken token);
    Task FailAsync(Guid sessionId, long messageId, string errorCode, CancellationToken token);
    Task<IntakeSession> SetInstructionAsync(long userId, long chatId, string instruction, CancellationToken token);
    Task<IntakeSession> SetStyleAsync(long userId, long chatId, RequestedReelsStyle style, CancellationToken token);
    Task<IntakeSession> SetDurationAsync(long userId, long chatId, int seconds, CancellationToken token);
    Task<IntakeCompletionResult> CompleteAsync(IntakeCompletion request, CancellationToken token);
    Task<IntakeCancellationResult> CancelAsync(long userId, long chatId, CancellationToken token);
    Task<PendingRecoveryResult> RecoverPendingAsync(CancellationToken token);
}
```

First test: reserve two messages in reverse completion order, accept both, reopen store, and assert items are ordered by Telegram `message_id`.

- [ ] **Step 2: Run the test and confirm RED**

Expected: compile failure because `ITelegramIntakeStore` and SQLite tables do not exist.

- [ ] **Step 3: Add intake tables in migration 12**

Create `telegram_intake_sessions`, `telegram_intake_items` and `telegram_intake_completion_receipts`. Assert a version-11 database upgrades to version 12 and a second initialization is idempotent. Include:

- one partial unique `Open` session per `user_id`;
- unique `(chat_id, message_id)` media receipt;
- unique `(chat_id, done_message_id)` completion receipt;
- persisted reservation and actual bytes;
- status checks for session and item enums;
- foreign keys to artifacts and jobs where values are non-null.

- [ ] **Step 4: Implement reservation and acceptance incrementally**

Add and run one test at a time for:

1. duplicate message returns existing item;
2. duplicate `file_unique_id` does not consume a slot;
3. duplicate SHA-256 converts the later item to `Failed` and frees reservation;
4. sixth video is rejected;
5. reservation above 524 288 000 bytes is rejected;
6. accepted duration above 600 seconds is rejected and reservation is freed;
7. unknown-size reservation uses the available per-file maximum;
8. reservation expansion fails before additional bytes are written.

- [ ] **Step 5: Implement settings, cancel and recovery**

Tests must prove:

- instruction replacement is persisted without logging its value;
- duration accepts only `5..60`;
- style accepts only the domain enum;
- cancel is idempotent;
- startup recovery changes every `Pending` item to `Failed` and returns temporary paths for safe deletion;
- accepted items survive recovery.

- [ ] **Step 6: Implement atomic completion and receipts**

`CompleteAsync` must return expected results for `NoVideos`, `InstructionMissing`, `UploadsPending`, `DurationUnavailable`, `Created` and `Existing`. Check `(chat_id, done_message_id)` receipt before selecting the current open session. Open one SQLite transaction, invoke `SqliteJobWriter` with that connection/transaction, then insert the completion receipt and session `job_id` before commit. Publish the queue signal only after commit.

- [ ] **Step 7: Run all intake integration tests**

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter "FullyQualifiedName~SqliteTelegramIntakeStoreTests"
```

Expected: PASS.

- [ ] **Step 8: Commit checkpoint**

```powershell
git add src/Modules/VideoProduction tests/Modules/VideoProduction
git commit -m "feat: persist telegram intake sessions"
```

---

### Task 4: Telegram Collection UX

**Files:**
- Modify: `src/VideoFactory.Bot/Telegram/TelegramIntakeModels.cs`
- Modify: `src/VideoFactory.Bot/Telegram/TelegramPollingService.cs`
- Modify: `src/VideoFactory.Bot/Telegram/TelegramUpdateHandler.cs`
- Modify: `src/VideoFactory.Bot/Telegram/TelegramBotAdapters.cs`
- Modify: `src/VideoFactory.Bot/VideoFactoryHost.cs`
- Test: `tests/VideoFactory.Bot.Tests/Telegram/TelegramUpdateHandlerTests.cs`
- Test: `tests/VideoFactory.Bot.Tests/Telegram/TelegramDownloaderReliabilityTests.cs`

**Interfaces:**
- Consumes: `ITelegramIntakeStore` from Task 3.
- Produces: command-driven sequential intake; no Job creation before `/done`.

- [ ] **Step 1: Write the first Telegram RED test**

```csharp
[Fact]
public async Task TwoVideosAndInstructionDoNotCreateJobBeforeDone()
{
    await handler.HandleAsync(VideoUpdate(messageId: 10, fileUniqueId: "a"), token);
    await handler.HandleAsync(VideoUpdate(messageId: 20, fileUniqueId: "b"), token);
    await handler.HandleAsync(TextUpdate(messageId: 30, "Собери обзор"), token);

    Assert.Equal(0, createJob.Calls);

    await handler.HandleAsync(TextUpdate(messageId: 40, "/done"), token);
    Assert.Equal(1, createJob.Calls);
    Assert.Equal(2, createJob.LastCommand!.SourceArtifactIds.Count);
}
```

- [ ] **Step 2: Run and confirm the old immediate-create behavior fails**

```powershell
dotnet test .\tests\VideoFactory.Bot.Tests\VideoFactory.Bot.Tests.csproj --filter "FullyQualifiedName~TelegramUpdateHandlerTests.TwoVideosAndInstructionDoNotCreateJobBeforeDone"
```

- [ ] **Step 3: Route commands without mixing business rules into Host**

The handler parses syntax only and calls typed intake transitions:

- `/start` -> status/help;
- `/style <name>` -> mapped `RequestedReelsStyle`;
- `/duration <integer>` -> `5..60` transition;
- `/done` -> completion result;
- `/cancel` -> cancellation result;
- ordinary text -> instruction replacement;
- media -> reserve, download/probe/hash, accept or fail.

Keep quota calculations in Application/Infrastructure, not in the handler.

- [ ] **Step 4: Make download honor reservations**

Pass the persisted reservation byte limit to the downloader. If the stream exceeds the reservation, request atomic expansion before writing more bytes. On any failure, call `FailAsync`, remove temporary files and preserve accepted items.

- [ ] **Step 5: Add command and idempotency tests one at a time**

Cover exact observable responses for:

- all five styles and unknown style;
- duration `4`, `5`, `60`, `61` and non-integer;
- `/done` with no video, no instruction and pending upload;
- repeated current `/done` returns the same Job;
- repeated old `/done` does not complete a newer session;
- duplicate media message/file/hash does not add an item;
- `/cancel` before `/done` never calls Job creation;
- restart uses a fresh SQLite store and retains accepted items/settings;
- unauthorized user is rejected before download or persistence.

- [ ] **Step 6: Replace in-memory registration**

Register `SqliteTelegramIntakeStore` as the production `ITelegramIntakeStore`. Remove `InMemoryTelegramIntakeSessionStore` only after all tests use the new seam. Startup invokes pending recovery and deletes only returned temporary intake paths under the configured workspace root.

- [ ] **Step 7: Run Telegram suites**

```powershell
dotnet test .\tests\VideoFactory.Bot.Tests\VideoFactory.Bot.Tests.csproj --filter "FullyQualifiedName~Telegram"
```

Expected: PASS with no real Bot API or token logging.

- [ ] **Step 8: Commit checkpoint**

```powershell
git add src/VideoFactory.Bot tests/VideoFactory.Bot.Tests
git commit -m "feat: collect telegram video sets"
```

---

### Task 5: Per-source Preparation and Artifact Lookup

**Files:**
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Storage/IArtifactStore.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Storage/SqliteArtifactStore.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Media/PreparationStageExecutor.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Pipeline/PreparationStageExecutorTests.cs`

**Interfaces:**
- Produces: source-aware artifact list/query and one immutable Master per Original.
- Consumes: ordered Job sources from Tasks 1-2.

- [ ] **Step 1: Write a RED recovery test with two Originals**

Prepare source A successfully, simulate interruption before source B registration, create a new executor, rerun, and assert:

```csharp
Assert.Equal(2, artifacts.Count(item => item.ArtifactType == "master-video"));
Assert.Single(artifacts, item => item.ParentArtifactId == sourceA);
Assert.Single(artifacts, item => item.ParentArtifactId == sourceB);
Assert.Equal(1, builder.CallsFor(sourceA));
Assert.Equal(2, builder.CallsFor(sourceB));
```

- [ ] **Step 2: Add a narrow source-aware query**

```csharp
Task<IReadOnlyList<ArtifactRecord>> ListAsync(
    JobId jobId,
    string artifactType,
    CancellationToken cancellationToken);
```

Do not add a generic repository abstraction. Select existing records by parent Original and verify path, hash, metadata and tool version before reuse.

- [ ] **Step 3: Iterate ordered sources in Preparation**

For each Original:

1. locate a valid Master with matching parent and input hash;
2. check storage before building;
3. build to a temporary path;
4. register immutable Master with deterministic idempotency metadata;
5. continue after valid completed sources;
6. finish the stage only after all sources have valid Masters.

- [ ] **Step 4: Add duplicate/retry/cancellation tests**

Verify repeated stage execution does not rebuild either Master, cancellation stops before the next source, and corrupt existing Master is not accepted as completion.

- [ ] **Step 5: Run Preparation tests**

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter "FullyQualifiedName~PreparationStageExecutorTests"
```

- [ ] **Step 6: Commit checkpoint**

```powershell
git add src/Modules/VideoProduction tests/Modules/VideoProduction
git commit -m "feat: prepare every job source"
```

---

### Task 6: Analysis V3 Aggregate

**Files:**
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Domain/Analysis/AnalysisResult.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Domain/Validation/AnalysisValidator.cs`
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Analysis/MultiSourceAnalysisContracts.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Analysis/AnalysisStageExecutor.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Analysis/AnalysisArtifactPublisher.cs`
- Modify: `contracts/analysis-result.schema.json`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Analysis/AnalysisValidatorTests.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Analysis/AnalysisStageExecutorTests.cs`

**Interfaces:**
- Produces: Analysis V3 `Sources[]`, legacy V1/V2 adapter and `analysis-source-result` artifacts.
- Consumes: per-source Masters from Task 5.

- [ ] **Step 1: Write Analysis V3 validator RED tests**

Build two sources with both local timelines starting at `0`. Assert valid source-local words pass, then independently assert these fail with stable codes:

- duplicate Original/Master pair;
- duplicate segment ID across sources;
- mapping combines Original A with Master B;
- word/scene/pause beyond its Master duration;
- feature references another source's segment or scene index.

- [ ] **Step 2: Introduce the V3 model and legacy adapter**

```csharp
public sealed record AnalysisSource(
    ArtifactId OriginalAssetId,
    ArtifactId MasterAssetId,
    ImmutableArray<SpeechSegment> Speech,
    ImmutableArray<MasterRange> Pauses,
    ImmutableArray<Scene> Scenes,
    AnalysisFeatureSet Features);

public sealed record MultiSourceAnalysisResult(
    int SchemaVersion,
    RevisionId RevisionId,
    ImmutableArray<AnalysisSource> Sources,
    ImmutableArray<AllowedSegment> AllowedSegments);
```

Use `SchemaVersion = 3`. Legacy V1/V2 deserialize through the existing contract dispatcher and adapt to exactly one `AnalysisSource`; do not rewrite persisted JSON.

- [ ] **Step 3: Update JSON Schema tests**

Add one positive two-source fixture and negative cross-source mapping/reference fixtures. Require V3 `Sources`, while preserving the existing V1/V2 schema branches.

- [ ] **Step 4: Persist per-source results before aggregation**

For each Master, publish `analysis-source-result` with parent Master, input hash and tool/contract version. Reuse only a verified matching artifact. Publish the aggregate `analysis-result` after all ordered source results exist, keyed by ordered source result hashes and Analysis version.

- [ ] **Step 5: Add restart tests**

Simulate interruption after source A analysis. A fresh executor must reuse A, analyze B once and publish exactly one aggregate V3 artifact. A second complete run must invoke no analyzer.

- [ ] **Step 6: Run Analysis tests**

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.UnitTests\VideoFactory.VideoProduction.UnitTests.csproj --filter "FullyQualifiedName~Analysis"
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter "FullyQualifiedName~AnalysisStageExecutorTests"
```

- [ ] **Step 7: Commit checkpoint**

```powershell
git add contracts src/Modules/VideoProduction tests/Modules/VideoProduction
git commit -m "feat: aggregate source-scoped analysis"
```

---

### Task 7: Source-aware Skills, Multi-Clip Director and Planning

**Files:**
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/EditingContracts.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/StoryAndPacingSkills.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/CaptionDirectorSkill.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/SmartReframeSkill.cs`
- Create: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Editing/MultiClipDirectorSkill.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Application/Planning/EditPlanPlanner.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Planning/PlanningStageExecutor.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.UnitTests/Editing/MultiClipDirectorSkillTests.cs`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Planning/PlanningStageExecutorTests.cs`

**Interfaces:**
- Produces: `MultiClipDecision` with ordered segment selections, source/output mappings and reasons.
- Consumes: Analysis V3 and full Original/Master catalog.

- [ ] **Step 1: Add a RED source-isolation regression test**

Create source A and B with identical numeric ranges `0..5_000_000`. Put a word only in A and assert Smart Cut/Caption does not attach it to B's segment.

- [ ] **Step 2: Introduce source-aware lookups**

Add methods on the editing context or Analysis V3 aggregate that require `MasterAssetId`:

```csharp
ImmutableArray<WordTiming> WordsFor(SourceMapping mapping);
ImmutableArray<MasterRange> PausesFor(ArtifactId masterAssetId);
ImmutableArray<Scene> ScenesFor(ArtifactId masterAssetId);
```

Update existing skills to use these methods. Do not copy source-filter logic into each skill.

- [ ] **Step 3: Write Multi-Clip Director RED tests**

Cover:

- two visually distinct sources both selected;
- confirmed duplicate group keeps the higher scored segment;
- continuity can reorder sources;
- equal score uses upload ordinal as deterministic tie-breaker;
- every output interval maps to one known source interval;
- single-source context returns an applied compatible decision.

- [ ] **Step 4: Implement the closed decision contract**

```csharp
public sealed record MultiClipSelection(
    SegmentId SegmentId,
    int SourceOrder,
    SourceMapping Mapping,
    int OutputOrder,
    decimal Score);

public sealed record MultiClipDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    ImmutableArray<MultiClipSelection> Selections)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);
```

No model paths, arbitrary operations or generated code are allowed.

- [ ] **Step 5: Register the skill and use requested style**

Map `ProductionSettings.RequestedStyle` to Application `ReelsStyle`, resolve `Auto` after aggregate features, and persist requested/resolved style. Provider receives known selected segment IDs and durations only.

- [ ] **Step 6: Expand Planning stage assets**

Load every source Original and Master referenced by Analysis V3. Reject missing or mismatched source pairs as expected failures. Build one asset catalog; do not call another module's Infrastructure.

- [ ] **Step 7: Run Editing and Planning tests**

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.UnitTests\VideoFactory.VideoProduction.UnitTests.csproj --filter "FullyQualifiedName~Editing|FullyQualifiedName~Planning"
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter "FullyQualifiedName~PlanningStageExecutorTests"
```

- [ ] **Step 8: Commit checkpoint**

```powershell
git add src/Modules/VideoProduction tests/Modules/VideoProduction
git commit -m "feat: direct multi-source edits"
```

---

### Task 8: Edit Plan V3 and Multi-source Rendering

**Files:**
- Create: `contracts/edit-plan.v3.schema.json`
- Modify: `contracts/render-request.schema.json`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Domain/Validation/EditPlanContractDispatcher.cs`
- Modify: `src/Modules/VideoProduction/VideoFactory.VideoProduction.Infrastructure/Rendering/RenderingStageExecutor.cs`
- Modify: `render-host/src/contracts.ts`
- Modify: `render-host/src/validate.ts`
- Modify: `render-host/src/templates/DynamicComposition.tsx`
- Modify: `render-host/tests/validation.test.ts`
- Test: `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Rendering/RenderingStageExecutorTests.cs`
- Test: `tests/VideoFactory.Bot.Tests/Rendering/RenderHostProcessAdapterIntegrationTests.cs`

**Interfaces:**
- Produces: Edit Plan V3 `sources[]` and RenderRequest V2 source assets with optional audio path.
- Compatibility: V1/V2 plans still use RenderRequest V1.

- [ ] **Step 1: Add schema RED fixtures**

Positive fixture: two `sources`, two clips, each clip references a known Master. Negative fixtures:

- duplicate source IDs;
- clip references unknown Master;
- source range outside its Master;
- audio path attached to another source;
- V2 request missing `videoPath`;
- executable or unknown effect remains rejected.

- [ ] **Step 2: Define Edit Plan V3 and RenderRequest V2**

TypeScript shape:

```typescript
export interface RenderSourceAssetV2 {
  sourceAssetId: string;
  videoPath: string;
  audioPath?: string | null;
  frameRate: { numerator: number; denominator: number };
}

export interface RenderRequestV2 {
  schemaVersion: 2;
  jobId: string;
  variantId: string;
  plan: EditPlanV3;
  assets: RenderSourceAssetV2[];
  outputPath: string;
  encoding: EncodingSettings;
}
```

Edit Plan V3 keeps the closed captions/effects/audio/style model from V2 but replaces singleton `source` with required unique `sources[]` and source IDs on every clip.

- [ ] **Step 3: Update C# contract dispatch tests**

The dispatcher must read V1/V2/V3, reject future versions and validate source membership semantically. Existing V1/V2 positive fixtures must remain green unchanged.

- [ ] **Step 4: Make Rendering build per-source assets**

For every used Master:

1. find and probe its own video;
2. create/reuse its own normalized audio stem when audio exists;
3. use `audioPath = null` when no audio stream exists;
4. verify frame rate and paths independently;
5. serialize one V2 asset with Master ID;
6. key render idempotency by Job, Variant, plan hash and renderer config.

Remove `render.multiple_master_assets_unsupported` only after the source-specific audio path is complete.

- [ ] **Step 5: Execute source-specific video and audio in Remotion**

Select one asset by `clip.sourceAssetId`. Use its `videoPath` for `<OffthreadVideo muted>` and its optional `audioPath` for `<Audio>`. Convert source microseconds using that asset's frame rate. Missing audio produces silence, never fallback.

- [ ] **Step 6: Add real two-source render assertion**

Use two synthetic files with distinguishable frame colors and audio tones. Assert:

- first output interval contains source A marker/tone;
- second interval contains source B marker/tone;
- removed tone is absent;
- no black frames at the splice;
- output exists, has one video stream and required audio;
- duration, codec, full decode, loudness and true peak pass.

- [ ] **Step 7: Run Node and rendering tests**

```powershell
npm run typecheck
npm test
npm run build
npm run smoke-render
```

Run from `render-host`.

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter "FullyQualifiedName~RenderingStageExecutorTests"
dotnet test .\tests\VideoFactory.Bot.Tests\VideoFactory.Bot.Tests.csproj --filter "FullyQualifiedName~RenderHostProcessAdapterIntegrationTests"
```

- [ ] **Step 8: Commit checkpoint**

```powershell
git add contracts render-host src/Modules/VideoProduction tests
git commit -m "feat: render multi-source edit plans"
```

---

### Task 9: End-to-end Recovery, Quotas and Documentation

**Files:**
- Modify: `tests/VideoFactory.Bot.Tests/Acceptance/TelegramToDeliveryAcceptanceTests.cs`
- Modify: `scripts/Prepare-AcceptanceMedia.ps1`
- Modify: `contracts/media-oracle.v1.schema.json` only if existing oracle cannot express source identity
- Modify: `README.md`
- Modify: `docs/acceptance-media.md`
- Modify: `docs/mvp-acceptance-report.md`

**Interfaces:**
- Consumes: complete intake-to-delivery flow from Tasks 1-8.
- Produces: verified acceptance evidence and truthful documentation.

- [ ] **Step 1: Add end-to-end RED acceptance scenarios**

Scenario A:

1. send source A and B;
2. set instruction/style/duration;
3. assert no Job before `/done`;
4. restart Host/store;
5. send `/done`;
6. run worker through delivery fake;
7. assert one Job, ordered source lineage, one delivered MP4 and one receipt.

Scenario B cancels after two accepted sources and proves no Job, render or delivery exists.

Scenario C repeats source and `/done`, proving no duplicate item, Job or delivery.

- [ ] **Step 2: Add restart fault points**

Exercise fresh executors after:

- first source Master;
- first `analysis-source-result`;
- aggregate Analysis publication;
- plan revision save;
- first source audio stem;
- render artifact registration.

Each retry must reuse verified artifacts and deliver exactly once.

- [ ] **Step 3: Run media oracle assertions**

Set `ACCEPTANCE_MEDIA_ROOT` to the generated fixture directory and verify source markers, tone ranges, splice order, full decode and output metadata. Do not claim real Telegram API acceptance unless credentials and actual publication are present.

- [ ] **Step 4: Run the full verification matrix**

```powershell
dotnet build .\Ai_Video_Creator.slnx --no-restore
.\scripts\Prepare-AcceptanceMedia.ps1 -OutputDirectory .\data\acceptance-media-v2
$env:ACCEPTANCE_MEDIA_ROOT = (Resolve-Path .\data\acceptance-media-v2).Path
dotnet test .\Ai_Video_Creator.slnx --no-build
```

In `render-host`:

```powershell
npm run typecheck
npm test
npm run build
npm run smoke-render
```

Then:

```powershell
git diff --check
```

Record exact pass/skip counts and explain every environment skip.

- [ ] **Step 5: Update documentation only with verified behavior**

Document:

- sequential 1-5 video UX and commands;
- exact byte/duration limits;
- Analysis/Edit Plan/RenderRequest versions;
- restart guarantees actually proven by tests;
- single-source compatibility;
- Telegram albums, real Bot API and other untested paths as explicit limits.

- [ ] **Step 6: Run High-only review**

Review current diff against issue #50, the design spec, AGENTS.md and security/idempotency requirements. Resolve every confirmed High finding and rerun affected tests.

- [ ] **Step 7: Commit checkpoint**

```powershell
git add README.md docs scripts contracts tests
git commit -m "test: accept multi-source telegram flow"
```

---

## Completion Gate

Issue #50 is ready to close only when:

- migration 11 and recovery tests pass;
- Telegram `1/5`, duplicate, quota, cancel and restart cases pass;
- Analysis V3 and Edit Plan V3 positive/negative validation pass;
- two distinct sources produce one measured MP4 with correct frame/audio order;
- legacy single-source Job and V1/V2 contracts remain green;
- `.NET` build/tests and Node typecheck/tests/build/smoke pass;
- `git diff --check` passes;
- High-only review has no findings;
- final commits are integrated into current `origin/main`, pushed and issue #50 is closed only with explicit Git authorization.
