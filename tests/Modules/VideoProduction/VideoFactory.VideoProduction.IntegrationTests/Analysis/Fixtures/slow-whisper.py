"""Управляемый внешний процесс для проверки отмены через IWhisperTranscriber."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time


parser = argparse.ArgumentParser()
parser.add_argument("--input", required=True, type=Path)
parser.add_argument("--duration-microseconds", required=True)
arguments = parser.parse_args()
child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
temporary = arguments.input.with_suffix(".tmp")
temporary.write_text(json.dumps([os.getpid(), child.pid]), encoding="utf-8")
temporary.replace(arguments.input)
time.sleep(60)
