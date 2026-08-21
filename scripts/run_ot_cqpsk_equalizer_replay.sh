#!/usr/bin/env bash
set -euo pipefail

# Offline-only same-IQ CQPSK equalizer experiment. This script never connects
# to, deploys to, or restarts a production host.

mode=${1:-screen}
selected_candidate=${2:-}
capture_root=/mnt/c/temp/pizzawave-rf/ot-day-night-replay
artifact_root=/mnt/c/temp/pizzawave-rf/ot-cqpsk-equalizer
source_root=/home/lilhoser/tr-equalizer-src
build_root=/home/lilhoser/tr-equalizer-build
binary="$build_root/trunk-recorder"
expected_commit=0535b6f62b3ca2d2747a87342cd5631641a8997d
parallelism=8

development=(
  1784584105012
  1784598296012
  1784754546018
  1784826012155
)
holdout=(
  1784776632020
  1784869903087
  1784971061020
  1784978579011
  1785014268017
  1785055888084
  1785057481013
  1785079468015
  1785233071011
  1785274447026
  1785317344016
  1785317659015
)
hamilton_holdout=(
  1784869903087
  1784978579011
  1785055888084
  1785079468015
  1785233071011
  1785317344016
)

case "$mode" in
  screen)
    captures=("${development[@]}")
    candidates=(
      baseline
      cma1_s1e4
      cma3_s1e5 cma3_s1e4 cma3_s5e4
      cma7_s1e5 cma7_s1e4 cma7_s5e4
      cma15_s1e5 cma15_s1e4
    )
    repetitions=1
    ;;
  confirm)
    captures=("${development[@]}")
    candidates=(baseline cma1_s1e4 cma3_s1e5 cma7_s1e4)
    repetitions=2
    ;;
  agc-validate)
    captures=("${hamilton_holdout[@]}")
    candidates=(baseline secondagc)
    repetitions=2
    ;;
  validate)
    if [[ -z "$selected_candidate" ]]; then
      echo "validate mode requires the selected candidate name" >&2
      exit 2
    fi
    captures=("${holdout[@]}")
    candidates=(baseline cma1_s1e4 "$selected_candidate")
    repetitions=2
    ;;
  *)
    echo "usage: $0 screen | confirm | validate <candidate>" >&2
    exit 2
    ;;
esac

candidate_values() {
  case "$1" in
    baseline) echo "0 0" ;;
    cma1_s1e4) echo "1 0.0001" ;;
    cma3_s1e5) echo "3 0.00001" ;;
    cma3_s1e4) echo "3 0.0001" ;;
    cma3_s5e4) echo "3 0.0005" ;;
    cma7_s1e5) echo "7 0.00001" ;;
    cma7_s1e4) echo "7 0.0001" ;;
    cma7_s5e4) echo "7 0.0005" ;;
    cma15_s1e5) echo "15 0.00001" ;;
    cma15_s1e4) echo "15 0.0001" ;;
    secondagc) echo "0 0" ;;
    *) echo "unknown candidate: $1" >&2; return 2 ;;
  esac
}

actual_commit=$(git -C "$source_root" rev-parse HEAD)
if [[ "$actual_commit" != "$expected_commit" ]]; then
  echo "unexpected source commit: $actual_commit" >&2
  exit 1
fi
if [[ ! -x "$binary" ]]; then
  echo "missing experiment binary: $binary" >&2
  exit 1
fi

phase_root="$artifact_root/$mode"
mkdir -p "$phase_root/configs" "$phase_root/logs" "$phase_root/runs"

manifest="$phase_root/manifest.txt"
{
  echo "purpose=offline same-IQ CQPSK CMA equalizer $mode; production untouched"
  echo "source_commit=$actual_commit"
  echo "binary_sha256=$(sha256sum "$binary" | awk '{print $1}')"
  echo "decoder_sha256=$(sha256sum "$build_root/libgnuradio-op25_repeater.so" | awk '{print $1}')"
  echo "parallelism=$parallelism"
  echo "repetitions=$repetitions"
  echo "captures=${captures[*]}"
  echo "candidates=${candidates[*]}"
  echo "started_utc=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
} > "$manifest"

for trigger in "${captures[@]}"; do
  metadata=$(find "$capture_root" -maxdepth 1 -type f -name "$trigger-*-automatic.json" -print -quit)
  input=${metadata%.json}.fc32
  if [[ -z "$metadata" || ! -f "$input" ]]; then
    echo "missing input pair for $trigger" >&2
    exit 1
  fi
  center=$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(int(d.get("primaryControlChannelHz") or d.get("liveControlChannelHz")))' "$metadata")
  system=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["systemShortName"])' "$metadata")
  config="$phase_root/configs/$trigger.json"
  printf '%s\n' \
    '{' \
    '  "sources": [{' \
    "    \"center\": $center," \
    '    "rate": 96000,' \
    '    "digitalRecorders": 1,' \
    '    "analogRecorders": 0,' \
    '    "driver": "osmosdr",' \
    "    \"device\": \"file=$input,freq=$center,rate=96000,repeat=false,throttle=true\"" \
    '  }],' \
    '  "systems": [{' \
    "    \"control_channels\": [$center]," \
    '    "type": "p25",' \
    "    \"shortName\": \"$system-$trigger\"," \
    '    "modulation": "qpsk",' \
    '    "callLog": false,' \
    '    "recordUnknown": false,' \
    '    "collapseShadow": true' \
    '  }],' \
    '  "controlWarnRate": -1,' \
    '  "controlRetuneGracePeriod": 9999,' \
    '  "ver": 2' \
    '}' > "$config"
  sha256sum "$input" "$metadata" "$config" >> "$manifest"
done

wait_for_slot() {
  while (( $(jobs -pr | wc -l) >= parallelism )); do
    wait -n
  done
}

for candidate in "${candidates[@]}"; do
  read -r taps step <<< "$(candidate_values "$candidate")"
  for trigger in "${captures[@]}"; do
    for repetition in $(seq 1 "$repetitions"); do
      wait_for_slot
      config="$phase_root/configs/$trigger.json"
      run_dir="$phase_root/runs/$candidate-$trigger-r$repetition"
      log="$phase_root/logs/$candidate-$trigger-r$repetition.log"
      meta="$phase_root/logs/$candidate-$trigger-r$repetition.meta"
      mkdir -p "$run_dir"
      (
        cd "$run_dir"
        {
          echo "candidate=$candidate"
          echo "taps=$taps"
          echo "step=$step"
          echo "started_utc=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
        } > "$meta"
        set +e
        if [[ "$candidate" == "secondagc" ]]; then
          TR_REPLAY_P25_QPSK_SECOND_AGC=1 \
          LD_LIBRARY_PATH="$build_root" timeout 105s \
            "$binary" --config="$config" > "$log" 2>&1
        elif [[ "$taps" == "0" ]]; then
          LD_LIBRARY_PATH="$build_root" timeout 105s \
            "$binary" --config="$config" > "$log" 2>&1
        else
          TR_REPLAY_P25_QPSK_CMA_TAPS="$taps" \
          TR_REPLAY_P25_QPSK_CMA_STEP="$step" \
          LD_LIBRARY_PATH="$build_root" timeout 105s \
            "$binary" --config="$config" > "$log" 2>&1
        fi
        status=$?
        set -e
        {
          echo "exit_status=$status"
          echo "completed_utc=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
          echo "log_sha256=$(sha256sum "$log" | awk '{print $1}')"
        } >> "$meta"
        [[ $status -eq 1 ]]
      ) &
    done
  done
done
wait

echo "completed_utc=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)" >> "$manifest"
sha256sum "$phase_root"/logs/*.log "$phase_root"/logs/*.meta >> "$manifest"
date -u +%Y-%m-%dT%H:%M:%S.%NZ > "$phase_root/COMPLETE"
