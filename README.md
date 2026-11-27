# AI Video Factory

Персональный Telegram-агент для автоматического монтажа одного видео из одного-пяти исходников.

Пользователь последовательно отправляет видео, задаёт инструкцию обычным текстом и завершает набор командой `/done`. Система анализирует материалы, создаёт один монтажный план, рендерит результат через Remotion и возвращает проверенный MP4.

## Статус

Проект реализует и локально принимает MVP: Telegram intake, SQLite Job store, подготовку и анализ медиа, ограниченное планирование, Remotion render, QA, упаковку и доставку MP4. Сквозной сценарий проверен на локальных acceptance media и Whisper без реального Telegram Bot API и облачного LLM.

## Цель MVP

Первый рабочий сценарий:

1. Единственный разрешённый пользователь последовательно отправляет от одного до пяти видео Telegram-боту.
2. Пользователь добавляет или заменяет инструкцию, например: `Сделай динамично, убери паузы и добавь субтитры`.
3. Опциональные `/style` и `/duration` задают известный стиль и целевую длительность.
4. `/done` атомарно создаёт один Job; `/cancel` до этого отменяет набор без Job.
5. Система подготавливает и анализирует каждый source, затем создаёт один монтажный план.
6. Remotion рендерит один вертикальный MP4, FFmpeg проверяет файл.
7. Бот возвращает MP4 пользователю.

Критерий готовности MVP: из одного-пяти поддерживаемых исходников система без ручного монтажа создаёт и доставляет один технически исправный MP4.

Результаты локальной приёмки и измерения ресурсов зафиксированы в [отчёте MVP](docs/mvp-acceptance-report.md).
Воспроизводимые synthetic fixtures и media oracle дополнительно проверяют
source trim, порядок video/audio, чёрные кадры, timing captions и crop-safe marker.
Edit Plan V2 также сохраняет и исполняет детерминированные Caption, Visual,
Smart Reframe и Audio decisions. Профиль `instagram-reels` фиксирует
1080x1920, 30 FPS и H.264/AAC; styles `auto`, `expert-clean`, `dynamic-viral`,
`cinematic` и `premium-ads` настраивают одни и те же editing skills.
Новый pipeline сохраняет source-scoped Analysis V3 и Edit Plan V3 и передаёт
RenderRequest V2 с отдельными video/audio paths каждого используемого Master.
Новые ревизии публикуются как Edit Plan V4: hash учитывает ID, версию, статус
`Applied`/`Skipped`/`Unavailable` и причины решений всех 11 editing skills.
Reels Critic остаётся отдельным компонентом, а Manifest V2 связывает полную
critic/remount chain, утверждённую ревизию, render, QA и final MP4. Legacy
Analysis V1/V2, Edit Plan V1/V2/V3, Manifest V1 и RenderRequest V1 остаются
читаемыми.
Read-only команда `acceptance-evidence --job=<guid> --output=<path>` проверяет
эту цепочку, зарегистрированные размеры и SHA-256 файлов и успешную delivery
receipt без запуска Telegram, hosted services или миграций.

## Режимы работы

### Быстрый

Единственный режим Telegram-flow первого MVP. Короткое видео автоматически анализируется и превращается в готовый MP4 без промежуточного утверждения.

### После MVP

Доменные модели `Auto` и `Director` уже существуют, но не входят в первый прикладной сценарий. После MVP `Auto` сможет рекомендовать режим, а `Director` — добавлять brief, план сцен, B-roll и preview с подтверждением пользователя.

## Планируемые сценарии

- демонстрация товара;
- talking-head;
- экспертные ролики и уроки;
- видео с речью или без неё;
- аудио с будущей визуализацией;
- текстовый сценарий;
- монтаж по механике референса;
- смысловой B-roll.

Первый сквозной тест использует один короткий fixture поддерживаемого входного профиля. Остальные сценарии не входят в критерий готовности MVP и должны документироваться как доступные только после реализации и проверки.

Подготовка воспроизводимого ролика с речью, паузами и контрольных файлов описана в [инструкции по медиа для приёмки](docs/acceptance-media.md).

## Технологический стек

Основной стек проекта — **C# / .NET**. На нём реализуются Telegram-бот, доменная модель, очередь, хранение состояния, генерация монтажного плана, интеграции и управление всем конвейером.

| Область | Технология |
|---|---|
| Основное приложение | C# / .NET Generic Host |
| Telegram | Telegram.Bot |
| База и очередь заданий | SQLite |
| Видеоподготовка и QA | FFmpeg / FFprobe |
| Рендер графики | Remotion + React + TypeScript |
| Распознавание речи | Whisper worker |
| AI-планирование | Заменяемые LLM API adapters |
| Хранение файлов | Локальная файловая система |

Node.js и Python являются изолированными вспомогательными runtime. Они не владеют бизнес-состоянием и не дублируют оркестрацию .NET.

## Архитектура

```text
Telegram.Bot
    |
    v
C# / .NET Orchestrator
    |-- SQLite job store
    |-- local artifact storage
    |-- FFmpeg / FFprobe
    |-- Whisper worker
    |-- LLM provider adapters
    `-- edit plan generator
             |
             v
        JSON edit plan
             |
             v
       Node render-host
             |
             v
      Remotion + React
             |
             v
          MP4 QA
```

### Ответственность компонентов

| Компонент | Ответственность |
|---|---|
| Telegram.Bot | Приём файлов и комментариев, подтверждения, прогресс и доставка результата |
| C#/.NET | Единственная бизнес-оркестрация, очередь, состояния, правила и запуск workers |
| SQLite | Надёжное состояние заданий, вариантов, попыток и артефактов |
| FFmpeg/FFprobe | Диагностика, нормализация, обработка звука, кодирование и технический QA |
| Whisper | Транскрипция материалов с полезной речью |
| LLM | Интерпретация комментария и планирование только в рамках заданных контрактов |
| Node render-host | Изолированный интерфейс C# с Remotion |
| Remotion | Детерминированная визуализация утверждённого JSON-плана |
| Файловая система | Оригиналы, промежуточные файлы, планы, MP4 и manifests |

## Основные принципы

- Оригинальный файл никогда не изменяется.
- SQLite является источником истины для состояния заданий; `Channel` может использоваться только как сигнал воркеру.
- LLM не генерирует shell-команды, React-код или произвольные таймкоды.
- Монтажные решения передаются через версионируемый JSON-контракт.
- JSON проходит структурную и семантическую проверку до рендера.
- Время оригинала, очищенного master и выходного варианта не смешивается.
- Remotion отвечает за изображение и анимацию, а не за бизнес-логику.
- Облачные AI-провайдеры подключаются через заменяемые адаптеры.
- Перед платной операцией рассчитывается стоимость и применяется лимит задания.
- Финальным считается только MP4, который успешно прошёл автоматическую проверку.
- Внешний вид товара нельзя изменять генеративно без явного разрешения.

## Несколько вариантов после MVP

Первый MVP создаёт один MP4. После завершения этого сквозного сценария генератор может быть расширен до нескольких вариантов, которые должны отличаться не только цветом. Контролируемые оси будущего расширения:

- выбор и порядок допустимых фрагментов;
- первый кадр;
- длительность и темп;
- crop, zoom и скорость;
- стиль субтитров и текстовых акцентов;
- музыкальный профиль;
- тема и композиция;
- переходы и плотность графики.

Перед рендером слишком похожие планы должны отбрасываться или перестраиваться.

## Планируемая структура

```text
Ai_Video_Creator.slnx
src/
  VideoFactory.Bot/
  Modules/
    VideoProduction/
      VideoFactory.VideoProduction.Domain/
      VideoFactory.VideoProduction.Application/
      VideoFactory.VideoProduction.Infrastructure/
render-host/
contracts/
scripts/
tests/
  Modules/
    VideoProduction/
      VideoFactory.VideoProduction.UnitTests/
      VideoFactory.VideoProduction.IntegrationTests/
data/
  jobs/
```

Приложение строится как модульный монолит. Внутри каждого бизнес-модуля зависимости направлены от Infrastructure к Application и далее к Domain. `VideoFactory.Bot` служит Host, Telegram-адаптером и composition root, но не содержит бизнес-правил.

Каталог `data` является рабочим локальным хранилищем и не должен попадать в систему контроля версий.

## Локальная проверка

### Node render-host

`render-host` принимает только JSON-файл запроса и не владеет состоянием задания:

```powershell
npm install --prefix .\render-host
npm run typecheck --prefix .\render-host
npm run build --prefix .\render-host
npm test --prefix .\render-host
npm start --prefix .\render-host -- list-templates
npm start --prefix .\render-host -- validate --job .\contracts\fixtures\valid-render-request.json
npm run smoke-render --prefix .\render-host
```

После `npm run build` воспроизводимой точкой входа для .NET является
`render-host/dist/cli.js`. Её можно проверить из корня solution:

```powershell
node .\render-host\dist\cli.js list-templates
node .\render-host\dist\cli.js validate --job .\contracts\fixtures\valid-render-request.json
```

Команды `validate` и `render` печатают структурированные события JSON Lines в `stdout`, а диагностику — в `stderr`. `render` сначала создаёт MP4 во временном файле и только после успешного завершения переименовывает его в `outputPath`.

Восстановление зависимостей, сборка и запуск всех тестов solution:

```powershell
dotnet restore .\Ai_Video_Creator.slnx
dotnet build .\Ai_Video_Creator.slnx --no-restore
dotnet test .\Ai_Video_Creator.slnx --no-build
```

Узкий запуск тестов модуля `VideoProduction`:

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.UnitTests\VideoFactory.VideoProduction.UnitTests.csproj
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj
```

Проверка agent harness и форматирования diff:

```powershell
.\.harness\health.ps1
git diff --check
```

## Очерёдность разработки

Подробный последовательный backlog, начиная с создания .NET-проектов, находится в [`TASKS.md`](TASKS.md).

GitHub-доска с 20 этапами и чек-листами: [AI Video Factory MVP](https://github.com/users/DmitriyEgorov1989/projects/2).

## Конфигурация и диагностика

Host читает настройки из `appsettings.json`, `appsettings.{Environment}.json`, User Secrets, environment variables и аргументов командной строки. Более поздний источник переопределяет более ранний. В репозитории находятся только безопасные значения по умолчанию; токены и API-ключи туда добавлять нельзя.

Для локальной разработки задайте обязательные Telegram-настройки через User Secrets:

```powershell
dotnet user-secrets set "Telegram:BotToken" "<token>" --project src/VideoFactory.Bot
dotnet user-secrets set "Telegram:AllowedUserId" "123456789" --project src/VideoFactory.Bot
```

Для планирования подключён DeepSeek через прямой HTTP-адаптер. Host загружает локальный `.env` из текущего каталога или его родителей, если файл существует. В автоматических тестах используется подменённый HTTP, а реальный платный вызов выполняется только при заполненном лимите стоимости задания. Файл `.env` не отслеживается Git.

Скопируйте безопасный шаблон:

```powershell
Copy-Item .\.env.example .\.env
```

Затем укажите `AiApi__ApiKey` в локальном `.env`. Для имён вложенных настроек используется двойное подчёркивание. Переменные окружения процесса и аргументы командной строки имеют приоритет над `.env`.

Альтернативный способ — User Secrets:

```powershell
dotnet user-secrets set "AiApi:Provider" "deepseek" --project src/VideoFactory.Bot
dotnet user-secrets set "AiApi:ApiKey" "<deepseek-api-key>" --project src/VideoFactory.Bot
dotnet user-secrets set "AiApi:Model" "deepseek-flash" --project src/VideoFactory.Bot
```

Тарифы задаются в конфигурации в USD за один миллион токенов, чтобы их можно было обновить без изменения кода:

```text
AiApi:InputCacheHitPriceUsdPerMillionTokens
AiApi:InputCacheMissPriceUsdPerMillionTokens
AiApi:OutputPriceUsdPerMillionTokens
AiApi:MaxOutputTokens
AiApi:TimeoutSeconds
```

Вызов использует `https://api.deepseek.com/chat/completions`, JSON Output и передаёт провайдеру только пользовательский комментарий, извлечённый текст и известные разрешённые сегменты. Локальные пути, ключи и произвольные assets в запрос не передаются.

Для environment variables вложенные ключи разделяются двойным подчёркиванием:

```powershell
$env:Telegram__BotToken = "<token>"
$env:Telegram__AllowedUserId = "123456789"
$env:FFmpeg__FfmpegPath = "C:\Tools\ffmpeg\bin\ffmpeg.exe"
```

`Paths:WorkspaceRoot` задаёт базовый каталог для относительных путей к локальным workers и render-host. Значение `../..` в `src/VideoFactory.Bot/appsettings.json` указывает на корень solution при запуске через `dotnet run --project`.

Проверить окружение без запуска долгоживущего Host можно командой:

```powershell
dotnet run --project src/VideoFactory.Bot -- diagnose
```

Диагностика проверяет конфигурацию, наличие файлов и executable, версии FFmpeg/FFprobe и фактический запуск настроенной точки входа Node render-host. Whisper worker устанавливается по инструкции в `docs/whisper-worker.md`; перед запуском Host Node render-host должен быть собран отдельно.

| Exit code | Значение |
|---:|---|
| `0` | Диагностика успешна или Host штатно остановлен |
| `1` | Диагностика обнаружила отсутствующую обязательную зависимость |
| `2` | Некорректная команда или обязательная конфигурация |

Обычный запуск выполняет startup validation и затем ожидает остановки через `Ctrl+C`:

```powershell
dotnet run --project src/VideoFactory.Bot
```

При обычном запуске Telegram polling запускается для одного `Telegram:AllowedUserId`.
Пользователь отправляет от одного до пяти отдельных video/document messages,
непустую инструкцию и `/done`; только после `/done` создаётся один Job в режиме
`Fast`. Обычный текст заменяет инструкцию. Поддерживаются `/style auto|expert-clean|dynamic-viral|cinematic|premium-ads`,
`/duration 5..60`, `/cancel` и `/done`. Telegram albums и `media_group_id` не поддерживаются.
Для локального запуска задайте
`Telegram:BotToken` и `Telegram:AllowedUserId` через User Secrets или переменные окружения.

## Надёжность и безопасная очистка

Настройки хранения задаются в секции `Storage`:

| Ключ | Значение по умолчанию | Смысл |
|---|---:|---|
| `MinimumFreeBytes` | `1073741824` | Резерв свободного места после операции, в байтах (1 ГиБ) |
| `JobQuotaBytes` | `5368709120` | Бюджет файлов одного Job, в байтах (5 ГиБ), включая временные файлы и попытки |
| `CompletedJobRetentionDays` | `30` | Срок хранения каталогов успешно завершённых Job, в сутках UTC |

До загрузки проверяются заявленный размер и доступное место; при неизвестном размере
резервируется `Limits:MaxInputFileBytes`. Поток скачивания ограничивается по фактически
записанным байтам. До нормализации и рендера проверяется оценка будущих файлов,
во время этих операций бюджет повторно проверяется раз в секунду с отменой процесса
при превышении. Это прикладное ограничение, а не квота файловой системы:
между проверками возможен кратковременный рост. В Host совместно учитываются
`jobs/{jobId}` и `data/jobs/{jobId}`. Перед копированием итогового MP4 также проверяется бюджет.

Загрузки MVP принимают только известные MIME `video/mp4` и `video/quicktime`.
Имя документа должно иметь соответствующее расширение и не содержать компонентов пути.
Имя никогда не используется как локальный путь: файл получает собственное безопасное имя.
До принятия файла FFprobe проверяет фактический контейнер, потоки и входной профиль;
расширение `.mp4` само по себе не доказывает, что содержимое является видео.
Один intake session принимает не более пяти файлов, `524288000` байт суммарно
и `600` секунд суммарной проверенной длительности. Порядок определяется Telegram
`message_id`. Повтор `file_unique_id`, SHA-256 или `/done` не создаёт второй item
или Job. Незавершённый набор и committed Job восстанавливаются из SQLite после restart.

Packaging требует успешный QA, совпадение Job, Variant, утверждённой ревизии,
идентификатора render Artifact, SHA-256 и размера. При повторе после прерывания
используется существующий неизменённый результат. Перед доставкой hash проверяется
повторно; неполная финализация без manifest и изменённый MP4 не отправляются.
Необработанные сообщения внешних ошибок не выводятся в обычные журналы и QA report:
диагностика этапа хранится отдельно в SQLite. Manifest содержит только технические поля.

Предварительный просмотр очистки, без запуска Telegram и без удаления файлов:

```powershell
dotnet run --project src/VideoFactory.Bot -- cleanup
```

Применение очистки:

```powershell
dotnet run --project src/VideoFactory.Bot -- cleanup --apply
```

Команда требует существующую базу со схемой версии 5; сама команда не создаёт базу
и не применяет миграции. Для отдельного окружения можно передать настройки
`--Paths:WorkspaceRoot=<path>` и `--Storage:DataRoot=<path>`.
Миграция 5 добавляет `completed_at`, `cleanup_started_at` и `files_deleted_at`.
Для существующих завершённых Job время завершения берётся из `updated_at`;
для новых Job оно фиксируется при переходе в `Completed` после доставки.

За один запуск рассматривается не более 100 просроченных Job. Удаляются только
их каталоги `data/jobs/{jobId}` и `jobs/{jobId}`. Общие исходники в `jobs/intake`,
файлы вне этих каталогов и история в SQLite сохраняются. Retention каталогов
не является полным удалением персональных данных из SQLite или intake.
Job в очереди, выполняющиеся, ожидающие подтверждения, ошибочные и отменённые Job
очистка не затрагивает. Каталоги с чужими ссылками, активным lease, незавершённой
попыткой или файловыми ссылками также не удаляются.

Очистка сериализуется файловой блокировкой, а начало удаления фиксируется в SQLite.
После прерывания повторный запуск завершает удаление; отметка `files_deleted_at`
предотвращает повторное применение. Нельзя регистрировать новые Artifacts или создавать
новый Job из файлов, уже отмеченных к очистке. Вывод содержит только счётчики и байты.
Код завершения: `0` — успешный просмотр/очистка, `1` — ошибка хранения или очистки,
`2` — неверные аргументы/конфигурация.

Проверки отказов находятся в `tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/Reliability`
и `tests/VideoFactory.Bot.Tests/Telegram/TelegramDownloaderReliabilityTests.cs`.
Они используют временную SQLite, синтетические данные и подменённые внешние адаптеры/HTTP.
Ошибки AI проверяются через `IEditPlanProvider`; реальный облачный адаптер этим не подтверждается.
Дополнительно настоящий локальный render-host проверяется на Remotion failure, timeout и
безопасный retry в `RenderHostProcessAdapterIntegrationTests`. Recovery всех стадий от
`Preparation` до `Delivery`, различие `USER_CANCELLED`/`HOST_STOPPING` и проверки
целостности Master/render описаны в `docs/mvp-acceptance-report.md`.
Сбой сети после принятия файла Telegram, но до сохранения receipt, оставляет неопределённость
доставки: гарантия отсутствия повторного сообщения в этом окне не заявляется.

## Локальные agent skills

После clone или перемещения проекта из корня solution выполните:

```powershell
.\.harness\setup.ps1
.\.harness\health.ps1
```

Подробности находятся в [`.harness/README.md`](.harness/README.md).

## Документация для агентов

Правила дальнейшей реализации и архитектурные ограничения описаны в [`AGENTS.md`](AGENTS.md).
