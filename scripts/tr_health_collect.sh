#!/usr/bin/env bash
set -euo pipefail

WINDOW_MINUTES="${1:-5}"
SERVICE_NAME="${TR_SERVICE_NAME:-trunk-recorder}"
TR_CONFIG_PATH="${TR_CONFIG_PATH:-/etc/trunk-recorder/config.json}"
OUT_DIR="${TR_HEALTH_DIR:-/var/lib/pizzawave/tr-health}"
SUMMARY_CSV="${OUT_DIR}/summary_5m.csv"

mkdir -p "$OUT_DIR"

end_iso="$(date '+%Y-%m-%d %H:%M:%S')"
start_iso="$(date -d "${WINDOW_MINUTES} minutes ago" '+%Y-%m-%d %H:%M:%S')"
tmp_log="$(mktemp)"
trap 'rm -f "$tmp_log"' EXIT

journalctl -u "$SERVICE_NAME" --since "$start_iso" --until "$end_iso" --no-pager > "$tmp_log" || true

if [[ ! -f "$SUMMARY_CSV" ]]; then
  echo "ts_start,ts_end,scope,decode_lines,decode0,decode0_pct,avg_decode_rate,max_decode_rate,retunes,calls_started,calls_concluded,update_not_grant,no_tx_recorded,sample_stops,unable_source,tuningerr_samples,tuningerr_avg_abs_hz,tuningerr_max_abs_hz" > "$SUMMARY_CSV"
fi

metric_extract_scope() {
  local scope_name="$1"
  local source_log="$2"
  local sys_log="$source_log"
  if [[ "$scope_name" != "global" ]]; then
    sys_log="$(mktemp)"
    grep "\[$scope_name\]" "$source_log" > "$sys_log" || true
  fi

  local total_decode decode0 decode_nonzero decode0_pct avg_decode_rate max_decode_rate
  local retunes calls_started calls_concluded update_not_grant no_tx_recorded sample_stops unable_source
  local tuningerr_samples tuningerr_avg_abs_hz tuningerr_max_abs_hz

  total_decode="$(grep -c "Control Channel Message Decode Rate" "$sys_log" || true)"
  decode0="$(grep -c "Control Channel Message Decode Rate: 0/sec" "$sys_log" || true)"
  decode_nonzero=$((total_decode - decode0))
  decode0_pct="0.00"
  avg_decode_rate="0.00"
  max_decode_rate=0
  retunes="$(grep -c "Retuning to Control Channel" "$sys_log" || true)"
  calls_started="$(grep -c "Starting P25 Recorder" "$sys_log" || true)"
  calls_concluded="$(grep -c "Concluding Recorded Call" "$sys_log" || true)"
  update_not_grant="$(grep -c "Call was UPDATE not GRANT" "$sys_log" || true)"
  no_tx_recorded="$(grep -c "No Transmissions were recorded!" "$sys_log" || true)"
  sample_stops="$(grep -c "stopped receiving samples" "$sys_log" || true)"
  unable_source="$(grep -c "Unable to find a source" "$sys_log" || true)"

  if [[ "$total_decode" -gt 0 ]]; then
    decode0_pct="$(awk -v z="$decode0" -v t="$total_decode" 'BEGIN { printf "%.2f", (z*100.0)/t }')"
    rates_tmp="$(mktemp)"
    grep "Control Channel Message Decode Rate" "$sys_log" \
      | sed -E 's/.*Decode Rate: ([0-9]+)\/sec.*/\1/' > "$rates_tmp" || true
    if [[ -s "$rates_tmp" ]]; then
      avg_decode_rate="$(awk '{s+=$1; n+=1} END { if (n>0) printf "%.2f", s/n; else print "0.00" }' "$rates_tmp")"
      max_decode_rate="$(sort -n "$rates_tmp" | tail -n1)"
    fi
    rm -f "$rates_tmp"
  fi

  tuning_tmp="$(mktemp)"
  grep "TuningErr:" "$sys_log" | sed -E 's/.*TuningErr: ([+-]?[0-9]+) Hz.*/\1/' > "$tuning_tmp" || true
  tuningerr_samples=0
  tuningerr_avg_abs_hz="0.00"
  tuningerr_max_abs_hz="0"
  if [[ -s "$tuning_tmp" ]]; then
    tuningerr_samples="$(wc -l < "$tuning_tmp" | tr -d ' ')"
    tuningerr_avg_abs_hz="$(awk '{v=$1; if (v<0) v=-v; s+=v; n+=1} END { if (n>0) printf "%.2f", s/n; else print "0.00" }' "$tuning_tmp")"
    tuningerr_max_abs_hz="$(awk '{v=$1; if (v<0) v=-v; if (v>m) m=v} END { print (m+0) }' "$tuning_tmp")"
  fi
  rm -f "$tuning_tmp"

  echo "$start_iso,$end_iso,$scope_name,$total_decode,$decode0,$decode0_pct,$avg_decode_rate,$max_decode_rate,$retunes,$calls_started,$calls_concluded,$update_not_grant,$no_tx_recorded,$sample_stops,$unable_source,$tuningerr_samples,$tuningerr_avg_abs_hz,$tuningerr_max_abs_hz" >> "$SUMMARY_CSV"

  if [[ "$scope_name" != "global" ]]; then
    rm -f "$sys_log"
  fi
}

metric_extract_scope "global" "$tmp_log"

mapfile -t systems < <(
  sed -nE 's/.*\(([a-z]+)\)[[:space:]]+\[([^]]+)\].*/\2/p' "$tmp_log" \
  | sort -u
)

for sys in "${systems[@]:-}"; do
  [[ -z "${sys:-}" ]] && continue
  metric_extract_scope "$sys" "$tmp_log"
done

if command -v jq >/dev/null 2>&1 && [[ -f "$TR_CONFIG_PATH" ]]; then
  mapfile -t source_rows < <(
    jq -r '
      .sources
      | to_entries[]
      | .key as $idx
      | .value as $s
      | ($s.device // "") as $dev
      | ((try ($dev | capture("(?<kind>rtl|airspy)[=:](?<serial>[^,]+)") | "\(.kind)=\(.serial)") catch ("idx" + ($idx|tostring))) ) as $serial
      | ($s.center // 0) as $center
      | ($s.rate // 0) as $rate
      | ($center - ($rate/2)) as $lo
      | ($center + ($rate/2)) as $hi
      | "\($idx),\($serial),\($lo|floor),\($hi|floor)"
    ' "$TR_CONFIG_PATH" 2>/dev/null || true
  )

  for row in "${source_rows[@]:-}"; do
    IFS=',' read -r src_idx src_serial src_lo src_hi <<< "$row"
    [[ -z "${src_serial:-}" ]] && continue

    source_stops="$(grep -c "Source ${src_idx} has stopped receiving samples" "$tmp_log" || true)"

    src_tune_tmp="$(mktemp)"
    awk -v lo="$src_lo" -v hi="$src_hi" '
      /TuningErr:/ {
        if (match($0, /Freq:[[:space:]]*([0-9]+\.[0-9]+)/, f) && match($0, /TuningErr:[[:space:]]*([+-]?[0-9]+)/, t)) {
          hz = int((f[1] + 0.0) * 1000000.0 + 0.5)
          if (hz >= lo && hz <= hi) print t[1]
        }
      }
    ' "$tmp_log" > "$src_tune_tmp" || true

    src_tuning_samples=0
    src_tuning_avg_abs="0.00"
    src_tuning_max_abs="0"
    src_calls_concluded=0
    if [[ -s "$src_tune_tmp" ]]; then
      src_tuning_samples="$(wc -l < "$src_tune_tmp" | tr -d ' ')"
      src_calls_concluded="$src_tuning_samples"
      src_tuning_avg_abs="$(awk '{v=$1; if (v<0) v=-v; s+=v; n+=1} END { if (n>0) printf "%.2f", s/n; else print "0.00" }' "$src_tune_tmp")"
      src_tuning_max_abs="$(awk '{v=$1; if (v<0) v=-v; if (v>m) m=v} END { print (m+0) }' "$src_tune_tmp")"
    fi
    rm -f "$src_tune_tmp"

    echo "$start_iso,$end_iso,source:${src_serial},0,0,0.00,0.00,0,0,0,$src_calls_concluded,0,0,$source_stops,0,$src_tuning_samples,$src_tuning_avg_abs,$src_tuning_max_abs" >> "$SUMMARY_CSV"
  done
fi

chmod 644 "$SUMMARY_CSV" || true
