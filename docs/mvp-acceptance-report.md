# Отчёт о приёмке MVP

Дата локального запуска: 20 ноября 2026 года.

## Сквозной сценарий

Проверен сценарий:

```text
TelegramUpdateHandler(video + comment)
  -> SQLite Job
  -> FFprobe/FFmpeg preparation
  -> speech detection + local Whisper
  -> deterministic edit plan
  -> Remotion render-host
  -> MP4 QA and atomic packaging
  -> fake Telegram delivery
```

Основной acceptance-тест: `TelegramToDeliveryAcceptanceTests` — 2/2 теста прошли.
Положительный сценарий создал один Job, один Variant, один verified MP4 и доставил
ровно один набор байт. Отрицательный сценарий с повреждённым MP4 не отправил файл.
Реальный Telegram Bot API и облачный LLM в тесте не используются.

## Проверенные инварианты

- подтверждение стоимости не требуется при оценке ниже лимита и переводит Job в
  `AwaitingConfirmation` только при превышении лимита;
- отмена на тяжёлом этапе сохраняет попытку как cancelled, не переводит Job вперёд
  и не публикует результат;
- после остановки Job возобновляется с последнего завершённого этапа;
- повторная доставка использует существующий verified MP4 без повторного render;
- `planHash`, revision ID, render artifact, QA artifact, final artifact, manifest и
  фактически доставленные байты взаимно согласованы;
- повреждённый или неподдерживаемый вход не достигает доставки.

## Измерения fixture

Измерения выполнены вокруг локального запуска acceptance-теста на Windows.

| Метрика | Значение | Смысл |
|---|---:|---|
| Время запуска теста | 49,5 с | полный процесс `dotnet test` с двумя acceptance-сценариями |
| Пиковая рабочая память | 148 779 008 байт | максимум `WorkingSet64` процессов `dotnet`/`testhost` |
| Наблюдаемое CPU-время | 11,2 с | максимум накопленного `CPU` у наблюдаемых процессов |
| Размер acceptance-каталога до Job | 4 532 578 байт | 7 локальных файлов fixture, включая manifest |

Значения зависят от версии Windows, FFmpeg, Node.js, .NET, CPU и диска; они служат
базовой точкой для локальной acceptance-среды, а не нормативом производительности.

## Команды проверки

```powershell
npm run typecheck --prefix .\render-host
npm run build --prefix .\render-host
npm test --prefix .\render-host
dotnet test .\Ai_Video_Creator.slnx
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter FullyQualifiedName~AcceptanceMediaTests
dotnet test .\tests\VideoFactory.Bot.Tests\VideoFactory.Bot.Tests.csproj --filter FullyQualifiedName~TelegramToDeliveryAcceptanceTests
```

Полный набор .NET прошёл без падений: 254 теста прошли, 10 тестов без локальных
медиа были пропущены в запуске без переменных окружения. Acceptance-запуск с
`ACCEPTANCE_MEDIA_ROOT` и `WHISPER_TEST_PYTHON` прошёл отдельно.

## Oracle монтажного качества

23 ноября 2026 года добавлен отдельный versioned media oracle для объективной
проверки монтажных решений. Локально с `ACCEPTANCE_MEDIA_ROOT` проверены:

- portrait и landscape fixtures с 24, 30 и 60 кадрами/с;
- SHA-256, размеры, FFprobe metadata и полный decode;
- визуальные markers, RMS/peak dBFS и audio tones 440/660/880 Гц;
- пропуск запрещённого среднего source-интервала без протекания его звука;
- порядок клипов, отсутствие чёрного кадра на склейке, caption window и
  сохранение focal marker после crop;
- эквивалентность повторных frame/audio observations.

Проверка solution: 308 тестов прошли, 4 теста пропущены. Три Whisper integration
теста и положительный Telegram-to-delivery acceptance были пропущены из-за
отсутствующего `WHISPER_TEST_PYTHON`; отрицательный Telegram-сценарий прошёл.
Render-host: 14 тестов прошли, typecheck, build и smoke render завершились
успешно. Analysis scores не используются вместо объективных media assertions.

## Reels directors

24 ноября 2026 года Edit Plan V2 расширен контролируемыми presentation
decisions и доведён без потерь от planning revision до render-host:

- Caption Director сохраняет Unicode word timings, семантические группы,
  максимум две строки, active-word highlight, fade и нижнюю safe zone;
- Visual Director создаёт только закрытые zoom/highlight/transition effects с
  идентификатором исходного skill; конфликтующие transitions отклоняются;
- Smart Reframe исполняет сглаженные focal keyframes, ограничивает скачок crop
  и использует явный `fit` fallback без focal observations;
- Audio Director задаёт speech loudness/true peak, fades и optional FFmpeg
  denoise; ducking принимается только с известным music asset;
- профиль `instagram-reels` фиксирует 1080x1920, 30 FPS и H.264/AAC, а пять
  styles детерминированно различаются параметрами тех же directors.

Production revision сохраняет resolved style и typed decisions в существующем
`plan_json` без миграции SQLite. Старые V1 и V2 документы продолжают читаться;
revision без presentation по-прежнему рендерится через V1 path. При отсутствии
CV observations Smart Reframe не заявляет tracking и безопасно показывает
источник через fallback.

## Multi-source intake и render

25 ноября 2026 года реализован persistent Telegram intake для одного-пяти
отдельных видео. Job создаётся только после `/done`; SQLite сохраняет порядок по
`message_id`, instruction, style, duration, reservations, deduplication и receipt
финализации. Лимиты session: пять файлов, `524288000` байт и `600` секунд.

Pipeline подготавливает и анализирует sources независимо, публикует Analysis V3,
строит Edit Plan V3 и RenderRequest V2. Каждый используемый Master получает свой
video path, optional audio stem и frame rate; отсутствие audio означает тишину,
а не fallback на другой source. Render reuse зависит от plan hash, renderer config
и ordered source IDs/hashes. Legacy single-source и V1/V2 contracts остаются зелёными.

Проверка текущего worktree:

- .NET build: `0` warnings, `0` errors;
- unit: `153` passed;
- integration: `118` passed, `15` environment-dependent skipped;
- Bot: `83` passed, `3` environment-dependent skipped;
- render-host: `17` passed; typecheck, build и двухисточниковый smoke успешны;
- smoke подтвердил красный/440 Гц, затем синий/880 Гц, отсутствие удалённого
  1320 Гц, video/audio streams, длительность `2` секунды и полный decode.

Положительный multi-source Telegram-to-delivery test добавлен, но в этом запуске
пропущен из-за отсутствующих `ACCEPTANCE_MEDIA_ROOT` и `WHISPER_TEST_PYTHON`.
Реальный Telegram Bot API, albums/`media_group_id` и облачный LLM этим запуском
не проверялись. Поэтому сквозная реальная Telegram-публикация не заявляется.

## Read-only acceptance evidence

Команда `acceptance-evidence --job=<guid> --output=<path>` формирует JSON по
контракту `contracts/acceptance-evidence.v1.schema.json` и возвращает `0` для
`Passed`, `3` для `InProgress`, `1` для `Failed` или отсутствующего Job и `2`
для неверных аргументов/конфигурации. Она открывает только существующую SQLite
в read-only режиме, не запускает hosted services и не применяет миграции.

Локальный integration-тест подтверждает успешную сверку Manifest V2, 11 skill
IDs, critic chain, QA, delivery и файлов, а затем обнаруживает изменение final
MP4 по размеру и SHA-256. Это не является заявлением о выполненном реальном
Telegram или платном API вызове.

## Матрица отказов, отмены и восстановления

27 ноября 2026 года локально проверена recovery-матрица issue `#56`. Общий
`SequentialJobProcessor` восстанавливает оставшуюся `Running` attempt как
`stage.interrupted`, создаёт следующую попытку для того же `JobId` и `VariantId`
и продолжает с последней атомарно завершённой стадии.

| Стадия | Инъекция или checkpoint | Ожидаемый результат | Доказательство |
|---|---|---|---|
| Preparation | изменённый Original; отмена FFmpeg; restart после регистрации Master | изменённый вход отклонён, process tree остановлен, готовый Master переиспользован | `PreparationStageExecutorTests`, `SystemProcessRunnerTests` |
| Analysis | отсутствующий или изменённый Master; отмена Whisper с дочерним процессом | Analysis не запускается для изменённого Master; restart остаётся на Analysis | `AnalysisStageExecutorTests`, `WhisperProcessLifetimeTests` |
| Planning | provider timeout, HTTP 429/500/503, malformed response, невалидный repair | временные ошибки повторяются; malformed/invalid repair не создают approved revision | `EditPlanPlannerTests`, `DeepSeekEditPlanProviderTests` |
| Rendering | настоящий Remotion failure и timeout | output не публикуется; повтор с тем же request успешно создаёт один MP4 | `RenderHostProcessAdapterIntegrationTests.RealRemotionFailureAndTimeoutLeaveNoOutputAndRetrySucceeds` |
| Critique | незавершённая attempt и restart | тот же Variant продолжает Critique, завершённая попытка переиспользуется | `RestartRecoversInterruptedAttemptAtEveryStage`, `CritiqueStageExecutorTests` |
| Verification | изменённый render после регистрации | QA не запускается, QA report и final MP4 не создаются | `VerificationPrivacyTests.ChangedRenderIsRejectedBeforeQualityAssurance` |
| Packaging | failed/tampered QA; остановка между публикацией файла и регистрацией artifact | непроверенный MP4 не публикуется; повтор регистрирует один final artifact | `PackagingSafetyTests` |
| Delivery | временная ошибка отправки; restart до или после сохранённой receipt | существующий final используется без render; сохранённая успешная receipt исключает повтор | `StageFaultTests`, `TelegramToDeliveryAcceptanceTests` |

Для всех восьми стадий переход после незавершённой attempt проверяет
`SequentialJobProcessorTests.RestartRecoversInterruptedAttemptAtEveryStage`.
`HOST_STOPPING` оставляет Job восстанавливаемым и включает исходную
`process.cancelled` диагностику; `USER_CANCELLED` переводит Job в `Cancelled`.
Caller cancellation и timeout завершают parent и child process tree.

Перед Analysis и Verification зарегистрированные Master/render сверяются по
размеру в байтах и SHA-256. Полный FFmpeg decode использует `-nostdin` и
`-xerror`. `RenderRequest V2` по опубликованной JSON Schema принимает Edit Plan
V3 или V4; V4 дополнительно проходит полный audit 11 editing skills.

Ограничение Telegram остаётся прежним: если Telegram принял файл, но Host
остановился до сохранения receipt, протокол Bot API не позволяет доказать
exactly-once delivery. Эта неопределённость не создаёт второй Variant, approved
plan или final MP4, но может потребовать операторской сверки доставки.

Итоговая локальная проверка: solution build прошёл без warnings и errors;
`215` unit, `134` integration и `89` Bot тестов прошли, `18`
environment-dependent тестов пропущены без acceptance media и Whisper;
render-host typecheck, build, `18` тестов и smoke render прошли. Harness health
не прошёл из-за существующего `Pinned source license drift detected: agent-harness`;
vendored harness в рамках задачи не изменялся.
