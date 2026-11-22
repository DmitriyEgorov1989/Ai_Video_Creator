"""Проверки публичного CLI без установки модели и распознавания речи."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


WORKER = Path(__file__).resolve().parents[2] / "scripts" / "whisper-worker.py"


class WhisperWorkerTests(unittest.TestCase):
    def run_worker(self, backend, duration="25000000", input_exists=True, encoding="utf-8"):
        with tempfile.TemporaryDirectory(prefix="whisper protocol ") as directory:
            root = Path(directory)
            (root / "faster_whisper.py").write_text(backend, encoding="utf-8")
            source = root / "master with spaces.mp4"
            if input_exists:
                source.touch()
            environment = dict(os.environ, PYTHONPATH=str(root), PYTHONIOENCODING=encoding)
            return subprocess.run(
                [sys.executable, str(WORKER), "--input", str(source),
                 "--duration-microseconds", duration],
                capture_output=True, encoding="utf-8", env=environment, timeout=10,
            )

    def test_outputs_versioned_json_with_master_word_timestamps(self):
        result = self.run_worker('''
from types import SimpleNamespace as Item
print("backend import diagnostic")
class WhisperModel:
    def __init__(self, *args, **kwargs):
        pass
    def transcribe(self, path, **kwargs):
        assert kwargs["word_timestamps"] is True
        print("backend transcription diagnostic")
        word = Item(word=" Hello", start=2.125, end=2.75)
        return iter([Item(text=" Hello", start=2.125, end=3.5, words=[word])]), None
''')

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual({
            "schemaVersion": 1,
            "segments": [{"text": "Hello", "startMicroseconds": 2125000,
                          "endMicroseconds": 3500000,
                          "words": [{"text": "Hello", "startMicroseconds": 2125000,
                                     "endMicroseconds": 2750000}]}],
        }, json.loads(result.stdout))
        self.assertIn("backend import diagnostic", result.stderr)
        self.assertIn("backend transcription diagnostic", result.stderr)

    def test_failed_lazy_transcription_preserves_diagnostic_without_partial_json(self):
        result = self.run_worker('''
class WhisperModel:
    def __init__(self, *args, **kwargs):
        pass
    def transcribe(self, *args, **kwargs):
        def segments():
            print("decoding diagnostic")
            raise RuntimeError("decoder code 77: invalid audio")
            yield
        return segments(), None
''')

        self.assertEqual(1, result.returncode)
        self.assertEqual("", result.stdout)
        self.assertIn("whisper.failed: RuntimeError: decoder code 77: invalid audio", result.stderr)

    def test_rejects_invalid_input_before_loading_backend(self):
        for duration, exists in [("0", True), ("-1", True), ("25000000", False)]:
            with self.subTest(duration=duration, exists=exists):
                result = self.run_worker(
                    'raise RuntimeError("backend must not load")', duration, exists,
                )
                self.assertEqual(2, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertIn("whisper.invalid_input", result.stderr)
                self.assertNotIn("backend must not load", result.stderr)

    def test_rejects_invalid_master_intervals_without_publishing_json(self):
        for start, end, word_start, word_end in [
            (-0.1, 3, 1, 2), (1, 26, 1, 2), (2, 2, 2, 2),
            (1, 3, 0.5, 2), (1, 3, 2, 3.5), (1, 3, 2, 2),
        ]:
            with self.subTest(interval=(start, end, word_start, word_end)):
                result = self.run_worker(f'''
from types import SimpleNamespace as Item
class WhisperModel:
    def __init__(self, *args, **kwargs):
        pass
    def transcribe(self, *args, **kwargs):
        word = Item(word="word", start={word_start}, end={word_end})
        return iter([Item(text="word", start={start}, end={end}, words=[word])]), None
''')
                self.assertEqual(1, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertIn("whisper.failed: ValueError:", result.stderr)

    def test_clamps_small_word_boundary_rounding_error_to_segment(self):
        result = self.run_worker('''
from types import SimpleNamespace as Item
class WhisperModel:
    def __init__(self, *args, **kwargs):
        pass
    def transcribe(self, *args, **kwargs):
        word = Item(word=" word", start=0.99995, end=3.00005)
        return iter([Item(text=" word", start=1, end=3, words=[word])]), None
''')

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            {"text": "word", "startMicroseconds": 1_000_000,
             "endMicroseconds": 3_000_000,
             "words": [{"text": "word", "startMicroseconds": 1_000_000,
                        "endMicroseconds": 3_000_000}]},
            json.loads(result.stdout)["segments"][0],
        )

    def test_rejects_missing_word_timings_and_empty_text(self):
        for text, words in [(" ", "[]"), ("speech", "None"), ("speech", "[]"),
                            ("speech", '[Item(word=" ", start=1, end=2)]')]:
            with self.subTest(text=text, words=words):
                result = self.run_worker(f'''
from types import SimpleNamespace as Item
class WhisperModel:
    def __init__(self, *args, **kwargs):
        pass
    def transcribe(self, *args, **kwargs):
        return iter([Item(text={text!r}, start=1, end=3, words={words})]), None
''')
                self.assertEqual(1, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertIn("whisper.failed: ValueError:", result.stderr)

    def test_uses_utf8_for_text_and_diagnostics_independently_of_system_encoding(self):
        result = self.run_worker('''
from types import SimpleNamespace as Item
print("Проверка диагностики")
class WhisperModel:
    def __init__(self, *args, **kwargs):
        pass
    def transcribe(self, *args, **kwargs):
        word = Item(word=" Речь", start=1, end=2)
        return iter([Item(text=" Речь", start=1, end=2, words=[word])]), None
''', encoding="ascii")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("Речь", json.loads(result.stdout)["segments"][0]["text"])
        self.assertIn("Проверка диагностики", result.stderr)


if __name__ == "__main__":
    unittest.main()
