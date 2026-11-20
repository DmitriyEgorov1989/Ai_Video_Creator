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
сценарий `Telegram video + comment -> verified MP4 -> Telegram delivery` без
реального Bot API и облачного LLM:

```powershell
npm ci --prefix .\render-host
npm run build --prefix .\render-host
$env:ACCEPTANCE_MEDIA_ROOT = (Resolve-Path .\data\acceptance-media-v2).Path
$env:WHISPER_TEST_PYTHON = (Resolve-Path .\.venv\Scripts\python.exe).Path
dotnet test .\tests\VideoFactory.Bot.Tests\VideoFactory.Bot.Tests.csproj `
  --filter FullyQualifiedName~TelegramToDeliveryAcceptanceTests
```

Тест создаёт временную SQLite и каталог Job, подаёт видео и комментарий через
`TelegramUpdateHandler`, запускает последовательный processor и сохраняет
байты fake Telegram delivery. Основной сценарий требует локальную модель
Whisper; отрицательный сценарий повреждённого входа не отправляет MP4.
