#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <SharpEmu executable> <eboot.bin>" >&2
  exit 2
fi

emulator="$(realpath "$1")"
eboot="$(realpath "$2")"
log_dir="${GITHUB_WORKSPACE:-$(pwd)}/artifacts/emulator-smoke"
log_file="$log_dir/emulator.log"
display_number="${SHARPEMU_TEST_DISPLAY:-:99}"
emulator_pid=""
xvfb_pid=""

mkdir -p "$log_dir"
: > "$log_file"

cleanup() {
  if [[ -n "$emulator_pid" ]] && kill -0 "$emulator_pid" 2>/dev/null; then
    kill -INT "$emulator_pid" 2>/dev/null || true
    for _ in {1..20}; do
      kill -0 "$emulator_pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$emulator_pid" 2>/dev/null || true
    wait "$emulator_pid" 2>/dev/null || true
  fi
  if [[ -n "$xvfb_pid" ]]; then
    kill "$xvfb_pid" 2>/dev/null || true
    wait "$xvfb_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

is_running() {
  kill -0 "$emulator_pid" 2>/dev/null &&
    [[ "$(ps -o stat= -p "$emulator_pid" 2>/dev/null)" != Z* ]]
}

Xvfb "$display_number" -screen 0 1280x720x24 -nolisten tcp >"$log_dir/xvfb.log" 2>&1 &
xvfb_pid=$!
export DISPLAY="$display_number"

for _ in {1..50}; do
  xdpyinfo -display "$DISPLAY" >/dev/null 2>&1 && break
  sleep 0.1
done
if ! xdpyinfo -display "$DISPLAY" >/dev/null 2>&1; then
  echo "Virtual display did not start." >&2
  exit 1
fi

"$emulator" --log-level=debug "$eboot" >"$log_file" 2>&1 &
emulator_pid=$!

window_id=""
for _ in {1..30}; do
  if ! is_running; then
    wait "$emulator_pid" || status=$?
    echo "SharpEmu exited before its display became ready (status ${status:-0})." >&2
    tail -n 100 "$log_file" >&2
    exit 1
  fi

  window_id="$(xdotool search --onlyvisible --pid "$emulator_pid" 2>/dev/null | head -n 1 || true)"
  [[ -n "$window_id" ]] && break
  sleep 1
done

if [[ -z "$window_id" ]]; then
  echo "SharpEmu did not create a visible window within 30 seconds." >&2
  tail -n 100 "$log_file" >&2
  exit 1
fi

xdotool windowactivate --sync "$window_id"
for _ in {1..3}; do
  xdotool key --window "$window_id" Return
  sleep 1
done

# Surviving 60 seconds of active emulation is the regression assertion.
for _ in {1..60}; do
  if ! is_running; then
    wait "$emulator_pid" || status=$?
    echo "SharpEmu exited during the play interval (status ${status:-0})." >&2
    tail -n 100 "$log_file" >&2
    exit 1
  fi
  sleep 1
done

screenshot="$log_dir/game-screenshot.png"
if ! import -display "$DISPLAY" -window "$window_id" "$screenshot"; then
  echo "Could not capture the emulator window after the play interval." >&2
  exit 1
fi
if [[ ! -s "$screenshot" ]]; then
  echo "The captured emulator screenshot is empty." >&2
  exit 1
fi

fatal_pattern='\[CRITICAL\]|fatal error|unhandled exception|segmentation fault|core dumped|SharpEmu failed to run|\[DEBUG\] Exception:'
if grep -Eiq "$fatal_pattern" "$log_file"; then
  echo "SharpEmu emitted a fatal error during the play interval." >&2
  grep -Ein "$fatal_pattern" "$log_file" >&2 || true
  exit 1
fi

echo "SharpEmu stayed alive without fatal errors; captured $screenshot."
