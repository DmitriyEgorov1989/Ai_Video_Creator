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

Проверка solution: 287 тестов прошли, 4 теста пропущены. Три Whisper integration
теста и положительный Telegram-to-delivery acceptance были пропущены из-за
отсутствующего `WHISPER_TEST_PYTHON`; отрицательный Telegram-сценарий прошёл.
Render-host: 10 тестов прошли, typecheck, build и smoke render завершились
успешно. Analysis scores не используются вместо объективных media assertions.
