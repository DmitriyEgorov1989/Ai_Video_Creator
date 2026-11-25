# Issue #50: Multi-source и Telegram-набор материалов

## Назначение

Пользователь собирает в Telegram упорядоченный набор из одного-пяти видео,
задаёт инструкцию, стиль и целевую длительность, затем явно завершает набор.
Только после завершения создаётся один Job и запускается последовательный
production pipeline, создающий один проверенный MP4 из реальных источников.

## Пользовательские сценарии

### Успех

Пользователь отправляет два видео длительностью по 25 секунд, обычным текстом
задаёт инструкцию, отправляет `/style cinematic`, `/duration 30` и `/done`.
Система создаёт один Job, использует оба источника с корректными video/audio
интервалами и доставляет один MP4 длительностью около 30 секунд.

### Ожидание

Пользователь загрузил два видео, но не отправил `/done`. Job и тяжёлые процессы
не создаются. После перезапуска приложения оба материала, инструкция, стиль и
длительность восстанавливаются из SQLite.

### Ошибка

Повторная доставка Telegram message или повтор того же файла не добавляет
материал второй раз. Превышение лимита набора отклоняется до создания Job.
Команда `/cancel` устойчиво закрывает intake session и не запускает processing.

## Наблюдаемое изменение

До изменения Telegram intake хранит один source только в памяти, второе видео
заменяет первое, а любой непустой текст сразу создаёт Job. После изменения
обычный текст только сохраняет инструкцию, видео добавляются последовательно,
а Job создаётся идемпотентно только по `/done`.

Single-source production остаётся поддержанным, но также требует явного
завершения набора. Telegram albums и `media_group_id` не поддерживаются.

## Telegram UX

- видео добавляются отдельными сообщениями; upload order определяется
  возрастанием Telegram `message_id` внутри сохранённого `chat_id`, независимо
  от порядка завершения concurrent downloads;
- обычный непустой текст заменяет текущую инструкцию;
- `/style <name>` принимает `auto`, `expert-clean`, `dynamic-viral`,
  `cinematic` или `premium-ads`;
- `/duration <seconds>` принимает целое значение от 5 до 60 секунд;
- `/done` атомарно завершает набор и создаёт либо возвращает существующий Job;
- `/cancel` переводит открытую сессию в `Cancelled`;
- `/start` объясняет команды, ограничения и текущее состояние набора.

Стиль по умолчанию равен `auto`. Если duration не задан, Reels profile выбирает
ближайшую завершённую semantic boundary около 30 секунд.
При `/done` явно заданная duration не должна превышать суммарную проверенную
длительность source files; иначе набор остаётся `Open` с понятной ошибкой, без
looping, неявного clamp и создания Job. Если после Analysis пригодных
`AllowedSegments` недостаточно, Planning возвращает ожидаемый отказ без рендера.

## Лимиты

Одна intake session принимает не более:

- 5 видео;
- 524 288 000 байт суммарного фактического размера (500 МиБ);
- 10 минут суммарной проверенной длительности.

Известный заявленный размер проверяется до скачивания. До начала download item
атомарно получает состояние `Pending`, резервирует один слот и заявленные байты.
Неизвестный размер резервирует доступный per-file maximum. Если фактический
stream превышает заявленный размер, reservation должен быть атомарно расширен
до продолжения записи; при недостатке квоты download прекращается. Stream
ограничивается минимумом из per-file limit и оставшейся квоты сессии.
После FFprobe и hash item переводится в `Accepted`, reservation заменяется
фактическими размером и длительностью. Ошибка переводит item в `Failed` и
освобождает reservation. `/done` запрещён при наличии `Pending` items и повторно
проверяет фактические aggregate limits. Существующие per-file media validation,
MIME/container checks, storage budget и безопасные имена сохраняются.

## Доменная модель Job

`JobRequest` и `CreateJobCommand` получают непустой упорядоченный набор
уникальных source artifact IDs. Порядок загрузки сохраняется как ordinal.
Первый source остаётся primary source только для чтения существующих записей;
авторитетным набором для нового кода является Job-to-Sources relation.

`ProductionSettings` сохраняет requested style вместе с target duration,
captions, music и tempo. Domain использует собственный enum стиля и не зависит
от Application `ReelsProfileCatalog`; Application выполняет явное сопоставление.

## SQLite и intake persistence

Новая migration добавляет:

- `job_sources(job_id, source_artifact_id, source_order, created_at)`;
- `telegram_intake_sessions` со статусом `Open`, `Completed` или `Cancelled`,
  user/chat identity, instruction, requested style, target duration, timestamps,
  completion identity и созданным `job_id`;
- `telegram_intake_items` с ordinal, artifact ID, Telegram message identity,
  `file_unique_id`, состоянием `Pending`, `Accepted` или `Failed`, reserved и
  actual bytes, SHA-256 и длительностью;
- сохраняемые `edit_tempo` и `requested_style` для Job.

`job_sources` имеет foreign keys на Job и Artifact,
`UNIQUE(job_id, source_order)` и `UNIQUE(job_id, source_artifact_id)`.
Partial unique index допускает только одну `Open` intake session на user ID.
Стабильный UUID сессии является её `completion_identity`; он создаётся один раз
при открытии session и не зависит от повторной доставки `/done`.
Отдельный completion receipt сохраняет `(chat_id, done_message_id, session_id,
job_id)`. Handler проверяет receipt до поиска текущей `Open` session, поэтому
повтор старой команды `/done` возвращает старый Job и не завершает новый набор.

Migration backfill создаёт ordinal `0` для каждого существующего
`jobs.source_artifact_id`. Migration пересоздаёт `jobs` без `UNIQUE` constraint
на legacy-столбце. Новые Job записывают туда source с ordinal `0` только для
старых readers, но новые stages не используют его как полный source set.
Единственным ключом идемпотентной Telegram-финализации является уникальный
`telegram_intake_sessions.completion_identity`. Defaults старых Job: tempo
`Dynamic`, style `Auto`.

В одной транзакции `/done` проверяет открытую сессию и квоты, создаёт Job,
Variant и ordered source links, привязывает Originals к Job, сохраняет `job_id`
в completed session и только затем публикует queue signal. Повторный `/done`
возвращает тот же Job.

До скачивания media message резервируется уникальным ключом `(chat_id,
message_id)`. Одинаковый материал внутри сессии определяется сначала по
Telegram `file_unique_id`, затем по SHA-256. Конкурирующие uploads не могут
независимо занять один остаток квоты. `/done` видит только полностью `Accepted`
items и при наличии `Pending` возвращает состояние ожидания без создания Job.

При startup recovery и `/cancel` оставшиеся `Pending` items атомарно переходят
в `Failed`, reservations освобождаются, временные файлы удаляются. Повторная
отправка создаёт новый item; завершённый `Accepted` item не теряется.

## Multi-source Analysis

Analysis V3 использует source-scoped aggregate:

```text
AnalysisResult
  Sources[]
    OriginalAssetId
    MasterAssetId
    Speech[]
      Words[]
    Scenes[]
    Pauses[]
    Features
  AllowedSegments[]
    SourceMapping
```

Каждый локальный таймкод интерпретируется только внутри своего Master.
`AllowedSegment.SourceMapping` сохраняет связь Original, Master и segment.
Validator требует уникальные пары Original/Master, глобально уникальные segment
IDs, принадлежность каждого mapping одной паре и границы всех speech, word,
scene, pause и feature references внутри соответствующего source. Локальные
`SpeechSegmentIds` и `SceneIndexes` не могут ссылаться на соседний source.
Analysis V1/V2 читаются legacy adapter-ом как V3 aggregate с одним source;
новый pipeline создаёт только V3.

Preparation создаёт Master отдельно для каждого Original. Analysis обрабатывает
каждый Master отдельно и сохраняет immutable промежуточный
`analysis-source-result`. Master и source analysis используют idempotency key
`(jobId, sourceArtifactId, toolOrContractVersion, inputHash)`. Одна immutable
aggregate V3 revision публикуется из проверенных промежуточных результатов
только после готовности всех sources. Restart повторяет только отсутствующие
или невалидные per-source operations.

Aggregate Analysis использует idempotency key
`(jobId, orderedSourceResultHashes, AnalysisVersion)`. Render использует
`(jobId, variantId, approvedPlanHash, rendererVersionAndConfig)`. Перед
повторным использованием проверяются существование, размер, SHA-256 и media
metadata артефакта; несовпадение не считается завершённым этапом.

## Editing и planning

Existing skills получают source-aware access к speech, words, scenes и pauses.
Они не сравнивают голые интервалы разных Masters.

Новый `MultiClipDirectorSkill`:

- исключает подтверждённые cross-source дубли;
- оценивает continuity и единый tempo/style;
- формирует объяснимое решение со стабильными IDs и reasons;
- сохраняет явные source/output mappings;
- использует upload order как tie-breaker, но может переставлять сегменты ради
  связной истории.

Provider продолжает получать только известные segment IDs и durations, без
путей и provider DTO в Domain. Planning получает полный каталог Originals и
Masters и восстанавливает mappings локально.

## Rendering и audio

Rendering использует все и только те Masters, на которые ссылается утверждённый
план. Каждый Master probe-ится отдельно; mixed source FPS не интерпретируется
через один общий frame rate.

Для каждого использованного source создаётся собственный нормализованный audio
stem. Render request связывает clip с его video asset и source-specific audio
asset. Remotion выбирает оба по `sourceAssetId`, поэтому звук второго source не
берётся из первого stem.

Edit Plan V1/V2 продолжают читаться и рендериться. Новый pipeline создаёт Edit
Plan V3 с обязательным `sources[]`; каждый clip ссылается на один известный
Master ID. Legacy V2 singleton `source` преобразуется в массив из одного
элемента и не используется как multi-source provenance.

RenderRequest V2 содержит assets вида
`{ sourceAssetId, videoPath, audioPath?, frameRate }`. `sourceAssetId` всегда
равен Master ID. Отсутствие `audioPath` означает тишину для клипов этого source,
а не fallback на звук другого Master. RenderRequest V1 с `{ id, path,
frameRate }` остаётся читаемым для Edit Plan V1/V2.

| Контракт | Читается | Создаётся новым pipeline | Адаптация |
|---|---:|---:|---|
| Analysis V1/V2 | Да | Нет | Один legacy source преобразуется в Analysis V3 source |
| Analysis V3 | Да | Да | Native source-scoped aggregate |
| Edit Plan V1/V2 | Да | Нет | V2 singleton source преобразуется в `sources[1]` |
| Edit Plan V3 | Да | Да | Native multi-source plan |
| RenderRequest V1 | Да | Нет | Legacy assets для Edit Plan V1/V2 |
| RenderRequest V2 | Да | Да | Per-source video, optional audio и frame rate |

Render output metadata сохраняет полный ordered source set и hashes. Один
parent artifact не используется как единственное доказательство lineage.

## Ошибки, отмена и recovery

- ошибка одного файла оставляет session открытой и не удаляет принятые items;
- `/done` отклоняется без видео или инструкции и не создаёт частичный Job;
- сбой после commit Job, но до Telegram response восстанавливается через
  persisted session `job_id`;
- `/cancel` не удаляет artifacts, уже принадлежащие Job;
- orphan intake cleanup не удаляет утверждённые планы и пользовательские
  результаты;
- stages завершаются только при наличии корректных artifacts для всего ordered
  source set;
- retries и restart не создают второй Master, Analysis revision, render или Job.

## Acceptance trace

| Критерий | Кодовый seam | Проверка |
|---|---|---|
| Job принимает 1–N sources | `JobRequest`, `CreateJobCommand`, `IJobCreationStore` | Domain и SQLite tests для 1, 2 и 5 sources |
| Набор ждёт `/done` | persistent intake store и `TelegramUpdateHandler` | До `/done` нет Job и queue signal |
| Restart сохраняет intake | SQLite intake store | Новый store instance восстанавливает session/items |
| Duplicate не добавляется | intake add transition | Повтор message, `file_unique_id` и SHA-256 |
| Квоты соблюдаются | intake policy | count, 524 288 000 байт, reservations и 10 минут boundary tests |
| Analysis source-aware | versioned schema и validator | Positive/negative contract tests |
| Multi-Clip Director объясним | `IEditingSkill` | duplicate, continuity, tie-break и mapping tests |
| Heavy stages recoverable | Preparation/Analysis/Rendering | restart между stages без duplicate artifacts |
| MP4 использует два sources | render request и Remotion | frame markers и разные audio tones |
| Single-source совместим | legacy readers/render paths | migration и V1/V2 regression tests |
| Cancel не запускает Job | intake state transition | cancellation и repeated cancellation tests |

## Порядок реализации

1. Domain/Application contracts для ordered sources и requested style.
2. SQLite migration, backfill, persistent intake и атомарный finalize.
3. Telegram command UX, deduplication, quotas, cancel и restart tests.
4. Source-aware artifact queries и per-source Preparation.
5. Versioned source-scoped Analysis, aggregation и validators.
6. Existing source-aware skills и `MultiClipDirectorSkill`.
7. Multi-source Planning catalog и persisted requested/resolved style.
8. Per-source rendering probe/audio и versioned render contract.
9. Two-source frame/audio acceptance, recovery и compatibility matrix.
10. README и acceptance report с фактически пройденными проверками.

Каждый шаг выполняется короткими срезами `red -> green -> refactor` через
публичный seam. Migration применяется только в рамках уже полученного в issue
разрешения.

## Проверка

- релевантный test сначала с ожидаемым падением;
- `dotnet test` для Domain/Application/Infrastructure/Telegram slices;
- `dotnet build .\Ai_Video_Creator.slnx`;
- JSON positive/negative schema tests;
- Node tests, typecheck и build;
- короткий multi-source smoke render;
- FFprobe, полный decode, frame/audio assertions, loudness и true peak;
- restart/retry/cancellation tests;
- `git diff --check`;
- High-only review перед закрытием issue.

## Риски и ограничения

- одинаковые локальные таймкоды разных Masters нельзя сравнивать без source ID;
- один глобальный audio stem создаёт неверный звук и запрещён для multi-source;
- Telegram может повторять и переставлять сообщения, поэтому persistence и
  finalize должны быть транзакционными;
- aggregate quota проверяется по фактическим metadata, не по имени файла;
- старые SQLite rows требуют table rebuild и backfill без потери Job state;
- пользовательские instruction, metadata и transcript не становятся system
  instructions и не записываются в обычные логи;
- Telegram albums, параллельные sessions одного пользователя, несколько
  Variants, ZIP, web UI, новые dependencies и Reels Critic #52 вне scope.
