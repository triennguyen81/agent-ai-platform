#!/usr/bin/env bash
# Batch render 6 HTML templates → PNG using Chrome headless.
# Requires Chrome or Chromium on PATH. Works from Git Bash on Windows.

set -euo pipefail
cd "$(dirname "$0")"

CHROME_CANDIDATES=(
  "chrome"
  "chromium"
  "google-chrome"
  "/c/Program Files/Google/Chrome/Application/chrome.exe"
  "/c/Program Files (x86)/Google/Chrome/Application/chrome.exe"
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
)

CHROME=""
for c in "${CHROME_CANDIDATES[@]}"; do
  if command -v "$c" >/dev/null 2>&1 || [ -x "$c" ]; then
    CHROME="$c"
    break
  fi
done

if [ -z "$CHROME" ]; then
  echo "[!] Chrome/Chromium not found. Install one and add to PATH." >&2
  exit 1
fi

for f in *.html; do
  [ -f "$f" ] || continue
  out="${f%.html}.png"
  # Windows path (Git Bash): -W gives Windows-style path Chrome wants
  if command -v pwd >/dev/null && pwd -W >/dev/null 2>&1; then
    src="file:///$(pwd -W)/$f"
    dst="$(pwd -W)/$out"
  else
    src="file://$(pwd)/$f"
    dst="$(pwd)/$out"
  fi
  echo "→ $out"
  "$CHROME" \
    --headless=new \
    --disable-gpu \
    --hide-scrollbars \
    --window-size=1000,1600 \
    --screenshot="$dst" \
    "$src" 2>/dev/null || echo "  (render failed for $f)"
done

echo ""
ls -lh *.png
