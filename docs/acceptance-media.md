# Медиа для приёмки MVP

Набор для задачи №23 создаётся локально, без пользовательских материалов и облачных API.
Картинка генерируется FFmpeg; две нейтральные русские фразы из
`scripts/acceptance-speech.json` озвучивает установленный голос Windows
`Microsoft Irina Desktop`. Личное видео и сторонние записи не используются.
Использование системного голоса регулируется условиями установленной Windows;
пакет самого голоса в репозиторий не включается.

## Подготовка

Требуются Windows PowerShell 5.1, `System.Speech`, голос `Microsoft Irina Desktop`,
FFmpeg с `libx264`, `aac`, `mpeg4` и FFprobe в `PATH`.

Из корня проекта:

```powershell
.\scripts\Prepare-AcceptanceMedia.ps1
```

По умолчанию результат находится в `data/acceptance-media/`. Существующий каталог
сценарий не перезаписывает. Новую ревизию создавайте отдельно:

```powershell
.\scripts\Prepare-AcceptanceMedia.ps1 -OutputDirectory .\data\acceptance-media-v2
```

Для первоначальной локальной подготовки проверенная ревизия —
`data/acceptance-media-v2/`; первый каталог является предварительной ревизией.
Каталог `data/` исключён из Git. На другой машине файлы нужно создать заново.

## Содержимое

| Файл | Содержание | Ожидание через публичные интерфейсы приложения |
|---|---|---|
| `speech.mp4` | 25 секунд, 360×640 пикселей, MP4, H.264, AAC mono 48 000 Гц, SDR BT.709, постоянные 30 кадров/с | `IMediaProbe`, проверка профиля и полный decode успешны |
| `no-speech.mp4` | Та же картинка, аудиодорожка с тишиной на всём интервале 0–25 секунд | Вход поддерживается; материал для проверки сценария без полезной речи |
| `unsupported.mp4` | Основной ролик, видеокодек заменён на MPEG-4 Part 2; MP4 и AAC сохранены | Probe успешен, `MediaInputProfileValidator` возвращает `media.video_codec_unsupported` |
| `corrupt.mp4` | Первые 16 байтов основного файла: усечение внутри первого блока MP4 | `IMediaProbe` возвращает `process.exit_code` с диагностикой FFprobe |
| `phrase-0.wav`, `phrase-1.wav` | Исходные синтезированные фразы | Источники звука для воспроизведения и диагностики |
| `manifest.json` | Происхождение, версии инструментов, SHA-256, размеры в байтах, metadata и ожидаемые результаты | Hash и размер соответствуют фактическим файлам |
| `landscape.mp4` | Синтетический landscape-вариант с тем же содержимым | Проверка безопасного reframe и metadata |
| `portrait-24fps.mp4`, `portrait-60fps.mp4` | Portrait-варианты с постоянными 24 и 60 кадрами/с | Проверка frame-rate преобразований и допусков |
| `similar-take-a.mp4`, `similar-take-b.mp4` | Два похожих дубля с различимым marker-ом | Проверка выбора и порядка похожих фрагментов |
| `tones.mp4` | 10 секунд: 440 Гц на интервале 0–5 секунд и 880 Гц на интервале 5–10 секунд | Проверка порядка и непрерывности audio splice |
| `montage-source.mp4` | Три маркированных интервала: red/440 Гц, удаляемый yellow/660 Гц, blue/880 Гц | Oracle-driven проверка trim, порядка, audio leakage, captions, black frames и crop |
| `media-oracle.v1.json` | Версионированные markers, интервалы, hashes и допуски в микросекундах, кадрах и dB | Acceptance oracle для объективных media assertions |

Окна озвучки начинаются на 2-й и 13-й секунде. Их конец вычисляется из длительности
соответствующего WAV. Паузы: от 0 до 2 секунд, между концом первой фразы и
13-й секундой, от конца второй фразы до 25-й секунды. Точные значения находятся
в `speechWindows` и `pauseWindows` паспорта. Это окна фраз, а не пословная разметка:
голос может добавлять короткую тишину по краям и внутри фраз; для акустических
границ предусмотрен допуск 0,3 секунды.

## Проверка

Сценарий сам проверяет FFprobe и полный decode двух положительных файлов с
`-xerror`. Ошибка останавливает подготовку, итоговый паспорт не публикуется.
Наличие файлов без паспорта не означает успешного завершения подготовки.

Интеграционные тесты используют настоящие FFmpeg/FFprobe через application seam:

```powershell
$env:ACCEPTANCE_MEDIA_ROOT = (Resolve-Path .\data\acceptance-media-v2).Path
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter FullyQualifiedName~AcceptanceMediaTests
```

Проверяются основной профиль и decode, оба отрицательных примера, тишина
во всём файле без речи, hashes и размеры четырёх контрольных MP4.
Без `ACCEPTANCE_MEDIA_ROOT` эти тесты явно пропускаются, чтобы обычный набор тестов
не требовал локальных медиа и Windows-голоса. Заданная переменная с неверным
каталогом приводит к падению, а не к пропуску.

Воспроизводимость требует того же рецепта, версии голоса и кодировщиков.
Между версиями Windows/FFmpeg побайтовое совпадение не гарантируется:
сравнивайте характеристики, ожидаемые результаты и паспорт новой ревизии.
Время создания в паспорте при каждом запуске меняется.

`media-oracle.v1.json` является отдельным versioned acceptance-контрактом.
Он не заменяет `manifest.json`: manifest описывает генерацию и legacy-проверки,
а oracle описывает наблюдаемые признаки, по которым тесты будут проверять
монтажное качество. Motion, Sharpness и другие analysis scores не считаются
доказательством правильного trim, порядка клипов, audio splice или crop.

## Проверка Preparation через очередь

Этап `Preparation` подключён к `ProductionStageExecutor` и Host. Он проверяет
целостность Original, выполняет probe и проверку входного профиля, подготавливает
Master и только после регистрации результата переводит Job в `Analysis`.

Master сохраняется отдельным неизменяемым артефактом `master-video` в
`data/jobs/{jobId}/normalized/{artifactId}.mp4`. Для VFR или rotation используется
существующий mezzanine builder; для остальных поддерживаемых файлов создаётся
побайтовая копия без перекодирования. Копия нужна потому, что текущая схема SQLite
требует уникального пути каждого артефакта. Перед созданием файла проверяются
свободное место и квота Job. Original и его Telegram-метаданные не перезаписываются.

Запись Master содержит SHA-256, размер в байтах, родительский ID Original, версию
`preparation/1` и технические metadata с `schemaVersion = 1`: `originalSha256`,
`wasNormalized`, `originalMetadata`, `mediaMetadata`, `mapping`. В `mapping`
сохраняется существующий `TimelineMapping`: ID Original и Master, начало и
длительность обоих интервалов в целых микросекундах. Metadata и mapping
регистрируются одной записью, поэтому отдельного частично сохранённого mapping нет.

После остановки между регистрацией Master и переходом Job повтор проверяет hash
Original, наличие, размер и hash Master, принадлежность и согласованность mapping.
Завершённый результат используется без повторного запуска процессов. Если Master
отсутствует или повреждён, создаётся новая ревизия без перезаписи предыдущей.
Файл, опубликованный до сбоя регистрации, ещё не считается завершённым результатом;
повтор создаёт новый файл, а оставшийся входит в дисковую квоту и обычную очистку Job.

Проверка с временной SQLite через `SequentialJobProcessor.ProcessNextAsync`:

```powershell
dotnet test .\tests\Modules\VideoProduction\VideoFactory.VideoProduction.IntegrationTests\VideoFactory.VideoProduction.IntegrationTests.csproj --filter FullyQualifiedName~PreparationStageExecutorTests
```

Без `ACCEPTANCE_MEDIA_ROOT` выполняются проверки с подменёнными процессами.
При заданной переменной дополнительно выполняются два сценария с настоящими
FFmpeg/FFprobe: `speech.mp4` без перекодирования и его временная копия с поворотом
на 90 градусов, требующая нормализации. Для результата проверяются переход в
`Analysis`, hash, размер, потоки, длительность, dimensions, rotation, mapping
и полный decode с `-xerror`. Для создания повёрнутого входа нужен FFmpeg с
поддержкой `-display_rotation`.

Это подготовка входных материалов. Сквозная доставка MP4 через Telegram,
качество распознавания Whisper и удаление пауз проверяются отдельной приёмкой.

## Сквозная приёмка через подменённый Telegram

После сборки render-host и установки локальной Whisper-модели можно запустить
сценарий `Telegram videos + instruction + /done -> verified MP4 -> Telegram delivery` без
реального Bot API и облачного LLM:

```powershell
npm ci --prefix .\render-host
npm run build --prefix .\render-host
$env:ACCEPTANCE_MEDIA_ROOT = (Resolve-Path .\data\acceptance-media-v2).Path
$env:WHISPER_TEST_PYTHON = (Resolve-Path .\.venv\Scripts\python.exe).Path
dotnet test .\tests\VideoFactory.Bot.Tests\VideoFactory.Bot.Tests.csproj `
  --filter FullyQualifiedName~TelegramToDeliveryAcceptanceTests
```

Тест создаёт временную SQLite и каталог Job, подаёт два отдельных видео,
инструкцию и style через `TelegramUpdateHandler`, проверяет отсутствие Job до
`/done`, создаёт новый Telegram adapter/store поверх той же SQLite и только затем
финализирует intake. Последовательный processor должен сохранить ordered source
lineage, Analysis V3, Edit Plan V3, один verified MP4 и одну fake delivery receipt.
Основной сценарий требует локальную модель Whisper; без
`ACCEPTANCE_MEDIA_ROOT` и `WHISPER_TEST_PYTHON` он явно пропускается. Быстрые
сценарии cancel и duplicate `/done` не требуют внешних media. Отрицательный
сценарий повреждённого входа не отправляет MP4.

Render-host smoke отдельно создаёт два синтетических source с красным/синим
marker и тонами 440/880 Гц. Проверяются порядок кадров и звука, отсутствие
удалённого тона 1320 Гц, один video/audio stream, длительность и полный decode.
Это локальная проверка adapter-а, а не реальная публикация через Telegram Bot API.

## Реальная Telegram-приёмка

Для проверки настоящего Bot API и согласованного LLM-провайдера используйте
`scripts/Run-RealTelegramAcceptance.ps1`. Скрипт не принимает секреты в аргументах,
не печатает их и сохраняет только технические метрики. Перед запуском задайте
значения локально через environment variables или `.env`. Начните с безопасного
шаблона:

```powershell
Copy-Item .\.env.example .\.env
notepad .\.env
```

В `.env` укажите токен, разрешённый user ID, провайдера, ключ и модель. Реальные
значения не должны попадать в Git. `WHISPER__PythonPath` и
`WHISPER_MODEL_PATH` задаются в текущей PowerShell-сессии, потому что второй
параметр читает дочерний Python-процесс напрямую:

```powershell
$env:Telegram__BotToken = '<local secret>'
$env:Telegram__AllowedUserId = '123456789'
$env:AiApi__ApiKey = '<local secret>'
$env:AiApi__Provider = 'deepseek'
$env:AiApi__Model = 'deepseek-chat'
$env:Whisper__PythonPath = (Resolve-Path .\.venv\Scripts\python.exe).Path
$env:WHISPER_MODEL_PATH = (Resolve-Path .\data\models\whisper-small).Path
.\scripts\Run-RealTelegramAcceptance.ps1
```

После сообщения о запуске отправьте боту одно поддерживаемое видео и непустой
комментарий с разрешённого пользователя. Скрипт отслеживает дерево `dotnet`,
`node`, `python`, `ffmpeg` и `ffprobe`, а в
`data/real-telegram-acceptance-report.json` сохраняет время, пиковую RAM,
CPU-время и максимальный размер каталога `data`. Файл не содержит токенов,
комментария, содержимого медиа или Telegram file ID. Стоимость LLM и хэши
скачанного MP4 сверяются по SQLite, manifest и полученному файлу после
завершения сценария и фиксируются в отчёте без секретов.
