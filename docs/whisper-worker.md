# Whisper worker

`scripts/whisper-worker.py` — однократный внешний worker без состояния Job.
Он получает путь к Master-файлу, выполняет локальную транскрипцию и печатает один
версионированный JSON в `stdout`. Диагностика библиотек и ошибки печатаются в
`stderr`.

## Установка

Требуется Python 3.12 или совместимый Python 3.9+. Установка выполняется в
локальное виртуальное окружение, которое не попадает в Git:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r scripts/requirements-whisper.txt
```

В списке зафиксированы проверенные версии `faster-whisper==1.2.1` и совместимого
с ним `av==16.0.1`. Worker использует модель `small`, CPU и `int8`. Модель не
должна скачиваться во время обработки Job: заранее загрузите каталог
`Systran/faster-whisper-small` в `data/models/whisper-small` или задайте
`WHISPER_MODEL_PATH` с путём к локальному каталогу модели.

Пример предварительной загрузки:

```powershell
.\.venv\Scripts\hf.exe download Systran/faster-whisper-small `
  --local-dir data/models/whisper-small
```

Для запуска Host укажите интерпретатор виртуального окружения:

```powershell
$env:Whisper__PythonPath = (Resolve-Path .\.venv\Scripts\python.exe).Path
$env:WHISPER_MODEL_PATH = (Resolve-Path .\data\models\whisper-small).Path
dotnet run --project src/VideoFactory.Bot
```

## Протокол

```powershell
.\.venv\Scripts\python.exe scripts/whisper-worker.py `
  --input .\data\acceptance-media\speech.mp4 `
  --duration-microseconds 25000000
```

Успешный `stdout` содержит объект:

```json
{
  "schemaVersion": 1,
  "segments": [
    {
      "text": "Распознанная фраза",
      "startMicroseconds": 2000000,
      "endMicroseconds": 3500000,
      "words": [
        {
          "text": "Распознанная",
          "startMicroseconds": 2000000,
          "endMicroseconds": 2700000
        }
      ]
    }
  ]
}
```

Время выражено в микросекундах временной шкалы Master. Каждый сегмент и каждое
слово должны иметь непустой интервал; слова находятся внутри сегмента, а конец
сегмента не превышает `--duration-microseconds`. При ошибке worker возвращает
ненулевой код, не публикует частичный JSON и записывает сообщение с префиксом
`whisper.failed:` в `stderr`.

## Проверка

Протокольные проверки не требуют загрузки модели:

```powershell
python -m unittest discover -s tests/scripts -v
```

Для настоящей приёмки сначала создайте локальные материалы из
`docs/acceptance-media.md`, затем задайте две переменные:

```powershell
$env:WHISPER_TEST_PYTHON = (Resolve-Path .\.venv\Scripts\python.exe).Path
$env:ACCEPTANCE_MEDIA_ROOT = (Resolve-Path .\data\acceptance-media).Path
dotnet test tests/Modules/VideoProduction/VideoFactory.VideoProduction.IntegrationTests/VideoFactory.VideoProduction.IntegrationTests.csproj `
  --filter FullyQualifiedName~Whisper
```

Проверяются непустые сегменты и word timestamps, ошибки повреждённого файла,
тайм-аут, отмена и завершение дерева процессов.
