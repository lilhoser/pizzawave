#!/usr/bin/env bash
set -euo pipefail

# Unified Trunk Recorder tuning tool.
# Modes:
#  - ppm-convert: convert ppm to Hz error for a center frequency
#  - error-sweep: find best source error (Hz) for a system/control channel
#  - cc-sweep: find best control channel (and optionally gain/mod)
#  - device-bakeoff: compare SDR devices on same system/control channel
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  cat <<'EOF'
Usage:
  sudo ./tr_tune.sh <mode> [options]

Modes:
  ppm-convert     Convert ppm to Hz error
  error-sweep     Sweep source error values for one serial
  cc-sweep        Sweep control channels (and optional gain/mod)
  device-bakeoff  Compare SDR devices on same scenario

Global options (all sweep modes):
  --config PATH           (default: /etc/trunk-recorder/config.json)
  --service NAME          (default: trunk-recorder)
  --duration-sec N        (default: 240)
  --warmup-sec N          (default: 20)
  --output-dir PATH       (default: ./artifacts/tr-tune-<mode>-<timestamp>)
  --leave-last-config     do not restore baseline config on exit

Mode: ppm-convert
  --center-hz HZ          required
  --ppm VALUE             required (can be negative)
  --tr-sign positive|negative|native (default: native)

Mode: error-sweep
  --system NAME           required
  --control-channel HZ    required
  --device-serial SERIAL  required
  --template-serial SER   optional source row to clone center/rate from
  --base-error HZ         required
  --range-hz N            default 1200
  --step-hz N             default 300
  --modulation qpsk|fsk4  default qpsk
  --gain N                optional source gain override

Mode: cc-sweep
  --system NAME           required
  --mods CSV              default qpsk
  --gains CSV             default 24,28,32,36
  --cc-modes CSV          onecc,fullcc,eachcc (default eachcc)

Mode: device-bakeoff
  --system NAME           required
  --control-channel HZ    required
  --modulation qpsk|fsk4  default qpsk
  --template-serial SER   required (source row to clone center/rate/etc)
  --candidates CSV        required list: serial:error[:gain],...
                         example: 00000001:2312,00000002:2319:30

Examples:
  sudo ./tr_tune.sh ppm-convert --center-hz 855571875 --ppm -5 --tr-sign positive
  sudo ./tr_tune.sh error-sweep --system my-system --control-channel 855212500 --device-serial 00000005 --base-error 4278
  sudo ./tr_tune.sh error-sweep --system my-system --control-channel 855212500 --template-serial 00000005 --device-serial 00000006 --base-error 0
  sudo ./tr_tune.sh cc-sweep --system my-system --cc-modes eachcc --mods qpsk --gains 24,28,32,36
  sudo ./tr_tune.sh device-bakeoff --system my-system --control-channel 855212500 --template-serial 00000005 --candidates 00000005:4278,00000006:5146
EOF
}

need_root() {
  if [[ $EUID -ne 0 ]]; then
    echo "Run as root (sudo)." >&2
    exit 1
  fi
}

detect_real_user() {
  if [[ -n "${SUDO_USER:-}" && "$SUDO_USER" != "root" ]]; then
    REAL_USER="$SUDO_USER"
    REAL_GROUP="$(id -gn "$SUDO_USER" 2>/dev/null || echo "$SUDO_USER")"
    REAL_HOME="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
    [[ -n "$REAL_HOME" ]] || REAL_HOME="/home/$SUDO_USER"
  else
    REAL_USER="root"
    REAL_GROUP="root"
    REAL_HOME="$HOME"
  fi
}

require_tools() {
  for t in jq systemctl journalctl date awk sed grep; do
    command -v "$t" >/dev/null 2>&1 || { echo "Missing tool: $t" >&2; exit 1; }
  done
}

slug() { echo "$1" | tr ' /:' '___'; }

ts_now() { date '+%Y-%m-%d %H:%M:%S'; }

metric_extract() {
  # args: system log_file
  local sys="$1"
  local log_file="$2"
  local sys_log="$3"
  grep "\[$sys\]" "$log_file" > "$sys_log" || true

  local total_decode decode0 retunes calls_started calls_concluded update_not_grant no_tx_recorded
  local decode_nonzero decode0_pct avg_decode_rate max_decode_rate

  local rates_file
  rates_file="$(mktemp)"
  grep "Control Channel Message Decode Rate" "$sys_log" \
    | sed -E 's/.*Decode Rate: ([0-9]+)\/sec.*/\1/' >> "$rates_file" || true
  grep -E "\[$sys\][[:space:]]+[0-9]+(\.[0-9]+)?[[:space:]]+MHz[[:space:]]+-?[0-9]+(\.[0-9]+)?[[:space:]]+msg/sec" "$sys_log" \
    | sed -E 's/.*MHz[[:space:]]+(-?[0-9]+(\.[0-9]+)?)[[:space:]]+msg\/sec.*/\1/' >> "$rates_file" || true

  total_decode="$(grep -c . "$rates_file" || true)"
  decode0="$(awk '{ if (($1+0) == 0) z+=1 } END { print z+0 }' "$rates_file")"
  retunes="$(grep -c "Retuning to Control Channel" "$sys_log" || true)"
  calls_started="$(grep -c "Starting P25 Recorder" "$sys_log" || true)"
  calls_concluded="$(grep -c "Concluding Recorded Call" "$sys_log" || true)"
  update_not_grant="$(grep -c "Call was UPDATE not GRANT" "$sys_log" || true)"
  no_tx_recorded="$(grep -c "No Transmissions were recorded!" "$sys_log" || true)"

  decode_nonzero=0
  decode0_pct="0.00"
  avg_decode_rate="0.00"
  max_decode_rate=0

  if [[ "$total_decode" -gt 0 ]]; then
    decode_nonzero=$((total_decode - decode0))
    decode0_pct="$(awk -v z="$decode0" -v t="$total_decode" 'BEGIN { printf "%.2f", (z*100.0)/t }')"
    if [[ -s "$rates_file" ]]; then
      avg_decode_rate="$(awk '{s+=$1; n+=1} END { if (n>0) printf "%.2f", s/n; else print "0.00" }' "$rates_file")"
      max_decode_rate="$(sort -n "$rates_file" | tail -n1)"
    fi
  fi
  rm -f "$rates_file"

  echo "$total_decode,$decode0,$decode_nonzero,$decode0_pct,$avg_decode_rate,$max_decode_rate,$retunes,$calls_started,$calls_concluded,$update_not_grant,$no_tx_recorded"
}

run_pass() {
  # args: cfg_path service_name warmup duration sys out_log
  local cfg_path="$1"
  local service_name="$2"
  local warmup="$3"
  local duration="$4"
  local out_log="$5"

  systemctl restart "$service_name"
  sleep "$warmup"
  local start_ts end_ts
  start_ts="$(ts_now)"
  sleep "$duration"
  end_ts="$(ts_now)"
  journalctl -u "$service_name" --since "$start_ts" --until "$end_ts" --no-pager > "$out_log"
  echo "$start_ts,$end_ts"
}

BASE_CONFIG="/etc/trunk-recorder/config.json"
SERVICE_NAME="trunk-recorder"
DURATION_SEC=240
WARMUP_SEC=20
OUTPUT_DIR=""
LEAVE_LAST_CONFIG=0

MODE="${1:-}"
if [[ -z "$MODE" ]]; then
  usage
  exit 2
fi
shift || true

case "$MODE" in
  ppm-convert|error-sweep|cc-sweep|device-bakeoff) ;;
  -h|--help) usage; exit 0 ;;
  *) echo "Unknown mode: $MODE" >&2; usage; exit 2 ;;
esac

# Parse common first.
REM_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --config) BASE_CONFIG="$2"; shift 2 ;;
    --service) SERVICE_NAME="$2"; shift 2 ;;
    --duration-sec) DURATION_SEC="$2"; shift 2 ;;
    --warmup-sec) WARMUP_SEC="$2"; shift 2 ;;
    --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
    --leave-last-config) LEAVE_LAST_CONFIG=1; shift ;;
    *) REM_ARGS+=("$1"); shift ;;
  esac
done
set -- "${REM_ARGS[@]}"

if [[ "$MODE" == "ppm-convert" ]]; then
  CENTER_HZ=""
  PPM=""
  TR_SIGN="native"
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --center-hz) CENTER_HZ="$2"; shift 2 ;;
      --ppm) PPM="$2"; shift 2 ;;
      --tr-sign) TR_SIGN="$2"; shift 2 ;;
      -h|--help) usage; exit 0 ;;
      *) echo "Unknown arg: $1" >&2; exit 2 ;;
    esac
  done
  [[ -n "$CENTER_HZ" && -n "$PPM" ]] || { echo "ppm-convert requires --center-hz and --ppm" >&2; exit 2; }
  hz="$(awk -v c="$CENTER_HZ" -v p="$PPM" 'BEGIN { printf "%.0f", (c*p)/1000000.0 }')"
  case "$TR_SIGN" in
    native) out="$hz" ;;
    positive) out="$(awk -v h="$hz" 'BEGIN { if (h<0) h=-h; printf "%.0f", h }')" ;;
    negative) out="$(awk -v h="$hz" 'BEGIN { if (h>0) h=-h; printf "%.0f", h }')" ;;
    *) echo "Invalid --tr-sign: $TR_SIGN" >&2; exit 2 ;;
  esac
  echo "center_hz=$CENTER_HZ ppm=$PPM hz_native=$hz hz_tr($TR_SIGN)=$out"
  exit 0
fi

need_root
require_tools
detect_real_user

[[ -f "$BASE_CONFIG" ]] || { echo "Config not found: $BASE_CONFIG" >&2; exit 1; }
[[ -z "$OUTPUT_DIR" ]] && OUTPUT_DIR="$SCRIPT_DIR/artifacts/tr-tune-${MODE}-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUTPUT_DIR"
if [[ "$REAL_USER" != "root" ]]; then
  chown -R "$REAL_USER:$REAL_GROUP" "$OUTPUT_DIR" || true
fi

TMP_CFG="$(mktemp)"
BASELINE_CFG="$OUTPUT_DIR/config.baseline.json"
cp -f "$BASE_CONFIG" "$BASELINE_CFG"

restore_cfg() {
  if [[ "$LEAVE_LAST_CONFIG" -eq 0 ]]; then
    cp -f "$BASELINE_CFG" "$BASE_CONFIG"
    systemctl restart "$SERVICE_NAME" || true
  fi
  if [[ "$REAL_USER" != "root" ]]; then
    chown -R "$REAL_USER:$REAL_GROUP" "$OUTPUT_DIR" || true
  fi
  rm -f "$TMP_CFG"
}
trap restore_cfg EXIT

if [[ "$MODE" == "error-sweep" ]]; then
  SYSTEM_NAME=""
  CONTROL_CHANNEL=""
  DEVICE_SERIAL=""
  TEMPLATE_SERIAL=""
  BASE_ERROR=""
  RANGE_HZ=1200
  STEP_HZ=300
  MODULATION="qpsk"
  GAIN_OVERRIDE=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --system) SYSTEM_NAME="$2"; shift 2 ;;
      --control-channel) CONTROL_CHANNEL="$2"; shift 2 ;;
      --device-serial) DEVICE_SERIAL="$2"; shift 2 ;;
      --template-serial) TEMPLATE_SERIAL="$2"; shift 2 ;;
      --base-error) BASE_ERROR="$2"; shift 2 ;;
      --range-hz) RANGE_HZ="$2"; shift 2 ;;
      --step-hz) STEP_HZ="$2"; shift 2 ;;
      --modulation) MODULATION="$2"; shift 2 ;;
      --gain) GAIN_OVERRIDE="$2"; shift 2 ;;
      *) echo "Unknown arg: $1" >&2; exit 2 ;;
    esac
  done
  for v in SYSTEM_NAME CONTROL_CHANNEL DEVICE_SERIAL BASE_ERROR; do
    [[ -n "${!v}" ]] || { echo "Missing required arg: $v" >&2; exit 2; }
  done

  jq -e --arg sys "$SYSTEM_NAME" '.systems[] | select(.shortName == $sys)' "$BASELINE_CFG" >/dev/null || { echo "System not found: $SYSTEM_NAME" >&2; exit 1; }
  jq -e --arg s "rtl=${DEVICE_SERIAL}" '.sources[] | select(.device | contains($s))' "$BASELINE_CFG" >/dev/null || { echo "Source serial not found: $DEVICE_SERIAL" >&2; exit 1; }
  if [[ -n "$TEMPLATE_SERIAL" ]]; then
    jq -e --arg s "rtl=${TEMPLATE_SERIAL}" '.sources[] | select(.device | contains($s))' "$BASELINE_CFG" >/dev/null || { echo "Template serial not found: $TEMPLATE_SERIAL" >&2; exit 1; }
  fi

  SUMMARY="$OUTPUT_DIR/summary.csv"
  echo "system,cc,serial,mod,error_hz,start,end,total_decode,decode0,decode_nonzero,decode0_pct,avg_decode_rate,max_decode_rate,retunes,calls_started,calls_concluded,update_not_grant,no_tx_recorded" > "$SUMMARY"

  for err in $(seq $((BASE_ERROR - RANGE_HZ)) "$STEP_HZ" $((BASE_ERROR + RANGE_HZ))); do
    if [[ -n "$GAIN_OVERRIDE" ]]; then
      jq --arg sys "$SYSTEM_NAME" --argjson cc "$CONTROL_CHANNEL" --arg mod "$MODULATION" --arg ser "$DEVICE_SERIAL" --arg tser "$TEMPLATE_SERIAL" --argjson e "$err" --argjson g "$GAIN_OVERRIDE" '
        .controlRetuneLimit = 0
        | .controlWarnRate = -1
        | .systems = [ .systems[] | select(.shortName == $sys) ]
        | .systems[0].control_channels = [ $cc ]
        | .systems[0].modulation = $mod
        | if ($tser != "") then .sources = [ .sources[] | select(.device | contains("rtl=" + $tser)) ] | .sources[0].device = ("rtl=" + $ser + ",bias=1,buflen=65536") else . end
        | .sources |= map(if (.device | contains("rtl=" + $ser)) then .error=$e | .gain=$g else . end)
      ' "$BASELINE_CFG" > "$TMP_CFG"
    else
      jq --arg sys "$SYSTEM_NAME" --argjson cc "$CONTROL_CHANNEL" --arg mod "$MODULATION" --arg ser "$DEVICE_SERIAL" --arg tser "$TEMPLATE_SERIAL" --argjson e "$err" '
        .controlRetuneLimit = 0
        | .controlWarnRate = -1
        | .systems = [ .systems[] | select(.shortName == $sys) ]
        | .systems[0].control_channels = [ $cc ]
        | .systems[0].modulation = $mod
        | if ($tser != "") then .sources = [ .sources[] | select(.device | contains("rtl=" + $tser)) ] | .sources[0].device = ("rtl=" + $ser + ",bias=1,buflen=65536") else . end
        | .sources |= map(if (.device | contains("rtl=" + $ser)) then .error=$e else . end)
      ' "$BASELINE_CFG" > "$TMP_CFG"
    fi

    cp -f "$TMP_CFG" "$BASE_CONFIG"
    pass_log="$OUTPUT_DIR/pass_error_${err}.log"
    times="$(run_pass "$BASE_CONFIG" "$SERVICE_NAME" "$WARMUP_SEC" "$DURATION_SEC" "$pass_log")"
    start_ts="${times%,*}"; end_ts="${times#*,}"
    sys_log="$OUTPUT_DIR/pass_error_${err}.sys.log"
    m="$(metric_extract "$SYSTEM_NAME" "$pass_log" "$sys_log")"
    echo "$SYSTEM_NAME,$CONTROL_CHANNEL,$DEVICE_SERIAL,$MODULATION,$err,$start_ts,$end_ts,$m" >> "$SUMMARY"
  done

  BEST="$OUTPUT_DIR/best.txt"
  awk -F, '
    NR==1 {next}
    {
      samples=$8+0; decode0=$11+0; avgd=$12+0; concl=$16+0
      score=(samples > 0 ? (1000.0-decode0)*1000000 + avgd*1000 + concl : -1000000 + concl)
      if (!best || score > bestScore) { best=1; bestScore=score; bestLine=$0 }
    }
    END { if (best) print bestLine; else print "NO_ROWS" }
  ' "$SUMMARY" > "$BEST"
  echo "Summary: $SUMMARY"
  echo "Best row: $BEST"
  exit 0
fi

if [[ "$MODE" == "cc-sweep" ]]; then
  SYSTEM_NAME=""
  MODS_CSV="qpsk"
  GAINS_CSV="24,28,32,36"
  CC_MODES_CSV="eachcc"
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --system) SYSTEM_NAME="$2"; shift 2 ;;
      --mods) MODS_CSV="$2"; shift 2 ;;
      --gains) GAINS_CSV="$2"; shift 2 ;;
      --cc-modes) CC_MODES_CSV="$2"; shift 2 ;;
      *) echo "Unknown arg: $1" >&2; exit 2 ;;
    esac
  done
  [[ -n "$SYSTEM_NAME" ]] || { echo "Missing --system" >&2; exit 2; }
  jq -e --arg sys "$SYSTEM_NAME" '.systems[] | select(.shortName == $sys)' "$BASELINE_CFG" >/dev/null || { echo "System not found: $SYSTEM_NAME" >&2; exit 1; }

  IFS=',' read -r -a MODS <<< "$MODS_CSV"
  IFS=',' read -r -a GAINS <<< "$GAINS_CSV"
  IFS=',' read -r -a CC_MODES <<< "$CC_MODES_CSV"

  SUMMARY="$OUTPUT_DIR/summary.csv"
  echo "system,mod,cc_mode,cc_value,gain,start,end,total_decode,decode0,decode_nonzero,decode0_pct,avg_decode_rate,max_decode_rate,retunes,calls_started,calls_concluded,update_not_grant,no_tx_recorded" > "$SUMMARY"

  mapfile -t ALL_CCS < <(jq -r --arg sys "$SYSTEM_NAME" '.systems[] | select(.shortName == $sys) | .control_channels[]' "$BASELINE_CFG")
  [[ "${#ALL_CCS[@]}" -gt 0 ]] || { echo "No control_channels found for $SYSTEM_NAME" >&2; exit 1; }

  for mod in "${MODS[@]}"; do
    for gain in "${GAINS[@]}"; do
      for ccmode in "${CC_MODES[@]}"; do
        cc_list=()
        case "$ccmode" in
          onecc) cc_list=("${ALL_CCS[0]}") ;;
          fullcc) cc_list=("ALL") ;;
          eachcc) cc_list=("${ALL_CCS[@]}") ;;
          *) echo "Invalid cc mode: $ccmode" >&2; exit 2 ;;
        esac

        for ccv in "${cc_list[@]}"; do
          if [[ "$ccmode" == "fullcc" ]]; then
            jq --arg sys "$SYSTEM_NAME" --arg mod "$mod" --argjson g "$gain" '
              .controlRetuneLimit = 0
              | .controlWarnRate = -1
              | .systems = [ .systems[] | select(.shortName == $sys) ]
              | .systems[0].modulation = $mod
              | .sources |= map(.gain=$g)
            ' "$BASELINE_CFG" > "$TMP_CFG"
          else
            jq --arg sys "$SYSTEM_NAME" --arg mod "$mod" --argjson g "$gain" --argjson cc "$ccv" '
              .controlRetuneLimit = 0
              | .controlWarnRate = -1
              | .systems = [ .systems[] | select(.shortName == $sys) ]
              | .systems[0].modulation = $mod
              | .systems[0].control_channels = [ $cc ]
              | .sources |= map(.gain=$g)
            ' "$BASELINE_CFG" > "$TMP_CFG"
          fi

          cp -f "$TMP_CFG" "$BASE_CONFIG"
          pass_log="$OUTPUT_DIR/pass_${SYSTEM_NAME}_${mod}_${ccmode}_${ccv}_g${gain}.log"
          times="$(run_pass "$BASE_CONFIG" "$SERVICE_NAME" "$WARMUP_SEC" "$DURATION_SEC" "$pass_log")"
          start_ts="${times%,*}"; end_ts="${times#*,}"
          sys_log="$OUTPUT_DIR/pass_${SYSTEM_NAME}_${mod}_${ccmode}_${ccv}_g${gain}.sys.log"
          m="$(metric_extract "$SYSTEM_NAME" "$pass_log" "$sys_log")"
          echo "$SYSTEM_NAME,$mod,$ccmode,$ccv,$gain,$start_ts,$end_ts,$m" >> "$SUMMARY"
        done
      done
    done
  done

  BEST="$OUTPUT_DIR/best_eachcc.csv"
  awk -F, '
    NR==1 { next }
    $3!="eachcc" { next }
    {
      key=$1","$2","$5
      samples=$8+0; decode0=$11+0; avgd=$12+0; concl=$16+0
      score=(samples > 0 ? (1000.0-decode0)*1000000 + avgd*1000 + concl : -1000000 + concl)
      if (!(key in best) || score > s[key]) { s[key]=score; best[key]=$0 }
    }
    END {
      print "system,mod,gain,best_cc,row"
      for (k in best) {
        split(best[k], a, ",")
        print a[1]","a[2]","a[5]","a[4]","best[k]
      }
    }
  ' "$SUMMARY" > "$BEST"
  echo "Summary: $SUMMARY"
  echo "Best each-CC: $BEST"
  exit 0
fi

if [[ "$MODE" == "device-bakeoff" ]]; then
  SYSTEM_NAME=""
  CONTROL_CHANNEL=""
  MODULATION="qpsk"
  TEMPLATE_SERIAL=""
  CANDIDATES=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --system) SYSTEM_NAME="$2"; shift 2 ;;
      --control-channel) CONTROL_CHANNEL="$2"; shift 2 ;;
      --modulation) MODULATION="$2"; shift 2 ;;
      --template-serial) TEMPLATE_SERIAL="$2"; shift 2 ;;
      --candidates) CANDIDATES="$2"; shift 2 ;;
      *) echo "Unknown arg: $1" >&2; exit 2 ;;
    esac
  done
  for v in SYSTEM_NAME CONTROL_CHANNEL TEMPLATE_SERIAL CANDIDATES; do
    [[ -n "${!v}" ]] || { echo "Missing required arg: $v" >&2; exit 2; }
  done

  jq -e --arg sys "$SYSTEM_NAME" '.systems[] | select(.shortName == $sys)' "$BASELINE_CFG" >/dev/null || { echo "System not found: $SYSTEM_NAME" >&2; exit 1; }
  jq -e --arg s "rtl=${TEMPLATE_SERIAL}" '.sources[] | select(.device | contains($s))' "$BASELINE_CFG" >/dev/null || { echo "Template serial not found: $TEMPLATE_SERIAL" >&2; exit 1; }

  IFS=',' read -r -a CANDS <<< "$CANDIDATES"
  SUMMARY="$OUTPUT_DIR/summary.csv"
  echo "system,cc,mod,template_serial,test_serial,error_hz,gain,start,end,total_decode,decode0,decode_nonzero,decode0_pct,avg_decode_rate,max_decode_rate,retunes,calls_started,calls_concluded,update_not_grant,no_tx_recorded" > "$SUMMARY"

  for c in "${CANDS[@]}"; do
    # serial:error[:gain]
    IFS=':' read -r ser err maybe_gain <<< "$c"
    [[ -n "$ser" && -n "$err" ]] || { echo "Bad candidate: $c" >&2; exit 2; }

    if [[ -n "$maybe_gain" ]]; then
      jq --arg sys "$SYSTEM_NAME" --argjson cc "$CONTROL_CHANNEL" --arg mod "$MODULATION" --arg tser "$TEMPLATE_SERIAL" --arg ser "$ser" --argjson e "$err" --argjson g "$maybe_gain" '
        .controlRetuneLimit = 0
        | .controlWarnRate = -1
        | .systems = [ .systems[] | select(.shortName == $sys) ]
        | .systems[0].control_channels = [ $cc ]
        | .systems[0].modulation = $mod
        | .sources = [ .sources[] | select(.device | contains("rtl=" + $tser)) ]
        | .sources[0].device = ("rtl=" + $ser + ",bias=1,buflen=65536")
        | .sources[0].error = $e
        | .sources[0].gain = $g
      ' "$BASELINE_CFG" > "$TMP_CFG"
    else
      jq --arg sys "$SYSTEM_NAME" --argjson cc "$CONTROL_CHANNEL" --arg mod "$MODULATION" --arg tser "$TEMPLATE_SERIAL" --arg ser "$ser" --argjson e "$err" '
        .controlRetuneLimit = 0
        | .controlWarnRate = -1
        | .systems = [ .systems[] | select(.shortName == $sys) ]
        | .systems[0].control_channels = [ $cc ]
        | .systems[0].modulation = $mod
        | .sources = [ .sources[] | select(.device | contains("rtl=" + $tser)) ]
        | .sources[0].device = ("rtl=" + $ser + ",bias=1,buflen=65536")
        | .sources[0].error = $e
      ' "$BASELINE_CFG" > "$TMP_CFG"
    fi

    cp -f "$TMP_CFG" "$BASE_CONFIG"
    pass_log="$OUTPUT_DIR/pass_${SYSTEM_NAME}_cc${CONTROL_CHANNEL}_${ser}.log"
    times="$(run_pass "$BASE_CONFIG" "$SERVICE_NAME" "$WARMUP_SEC" "$DURATION_SEC" "$pass_log")"
    start_ts="${times%,*}"; end_ts="${times#*,}"
    sys_log="$OUTPUT_DIR/pass_${SYSTEM_NAME}_cc${CONTROL_CHANNEL}_${ser}.sys.log"
    m="$(metric_extract "$SYSTEM_NAME" "$pass_log" "$sys_log")"
    gain_val="${maybe_gain:-TEMPLATE}"
    echo "$SYSTEM_NAME,$CONTROL_CHANNEL,$MODULATION,$TEMPLATE_SERIAL,$ser,$err,$gain_val,$start_ts,$end_ts,$m" >> "$SUMMARY"
  done

  BEST="$OUTPUT_DIR/best.txt"
  awk -F, '
    NR==1 {next}
    {
      samples=$10+0; decode0=$13+0; avgd=$14+0; concl=$18+0
      score=(samples > 0 ? (1000.0-decode0)*1000000 + avgd*1000 + concl : -1000000 + concl)
      if (!best || score > bestScore) { best=1; bestScore=score; bestLine=$0 }
    }
    END { if (best) print bestLine; else print "NO_ROWS" }
  ' "$SUMMARY" > "$BEST"

  echo "Summary: $SUMMARY"
  echo "Best row: $BEST"
  exit 0
fi
