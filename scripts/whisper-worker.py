"""Однократное распознавание Master; stdout содержит только JSON версии 1."""

import argparse
from contextlib import redirect_stdout
import json
import os
from pathlib import Path
import sys


def transcribe(source, duration_microseconds):
    # Импорт и ленивый генератор библиотеки могут писать диагностику в stdout.
    with redirect_stdout(sys.stderr):
        from faster_whisper import WhisperModel

        model_path = os.environ.get(
            "WHISPER_MODEL_PATH",
            str(Path(__file__).resolve().parent.parent / "data" / "models" / "whisper-small"),
        )
        model = WhisperModel(
            model_path, device="cpu", compute_type="int8", local_files_only=True,
            cpu_threads=4, num_workers=1,
        )
        segments, _ = model.transcribe(str(source), word_timestamps=True)
        document = [
            {
                "text": segment.text.strip(),
                "startMicroseconds": round(segment.start * 1_000_000),
                "endMicroseconds": round(segment.end * 1_000_000),
                "words": [
                    {
                        "text": word.word.strip(),
                        "startMicroseconds": round(word.start * 1_000_000),
                        "endMicroseconds": round(word.end * 1_000_000),
                    }
                    for word in (segment.words or [])
                ],
            }
            for segment in segments
        ]
        for segment in document:
            if not segment["text"] or not segment["words"]:
                raise ValueError("Speech segment must contain text and word timings.")
            start = segment["startMicroseconds"]
            end = segment["endMicroseconds"]
            if not 0 <= start < end <= duration_microseconds:
                raise ValueError("Segment interval is outside Master duration or empty.")
            for word in segment["words"]:
                if not word["text"]:
                    raise ValueError("Word text cannot be empty.")
                if not start <= word["startMicroseconds"] < word["endMicroseconds"] <= end:
                    raise ValueError("Word interval is outside its segment or empty.")
        return document


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--duration-microseconds", required=True, type=int)
    arguments = parser.parse_args()
    if arguments.duration_microseconds <= 0 or not arguments.input.is_file():
        parser.error("whisper.invalid_input: require an existing file and positive duration in microseconds")
    try:
        segments = transcribe(arguments.input, arguments.duration_microseconds)
    except (ImportError, OSError, ValueError, RuntimeError) as error:
        print(f"whisper.failed: {type(error).__name__}: {error}", file=sys.stderr)
        return 1
    print(json.dumps({"schemaVersion": 1, "segments": segments}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
