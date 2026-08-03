#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOCK_FILE="$SCRIPT_DIR/native-dependencies.lock"
SOURCE_ROOT=""
PRINT_SOURCE_DIR=0

usage() {
    cat <<'EOF'
Usage: prepare_trunk_recorder_source.sh --source-root PATH [--print-source-dir]

Clones the exact commits in native-dependencies.lock and applies only the
checked-in patches whose SHA-256 hashes are recorded in that lock file.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --source-root)
            shift
            SOURCE_ROOT="${1:-}"
            ;;
        --print-source-dir) PRINT_SOURCE_DIR=1 ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage >&2
            exit 2
            ;;
    esac
    shift
done

if [[ -z "$SOURCE_ROOT" ]]; then
    echo "Error: --source-root PATH is required." >&2
    exit 2
fi
if [[ ! -f "$LOCK_FILE" ]]; then
    echo "Error: native dependency lock not found: $LOCK_FILE" >&2
    exit 1
fi

# shellcheck source=native-dependencies.lock
source "$LOCK_FILE"

require_commit() {
    local name="$1"
    local value="$2"
    if [[ ! "$value" =~ ^[0-9a-f]{40}$ ]]; then
        echo "Error: $name must be a full 40-character lowercase Git commit ID." >&2
        exit 1
    fi
}

verify_patch() {
    local component="$1"
    local relative_path="$2"
    local expected_hash="$3"
    local patch_path="$SCRIPT_DIR/$relative_path"
    local actual_hash

    if [[ ! "$expected_hash" =~ ^[0-9a-f]{64}$ ]]; then
        echo "Error: $component patch hash is not a complete SHA-256 value." >&2
        exit 1
    fi
    if [[ ! -f "$patch_path" ]]; then
        echo "Error: $component patch not found: $patch_path" >&2
        exit 1
    fi
    actual_hash="$(sha256sum "$patch_path" | awk '{print $1}')"
    if [[ "$actual_hash" != "$expected_hash" ]]; then
        echo "Error: $component patch hash mismatch: expected $expected_hash, got $actual_hash" >&2
        exit 1
    fi
}

require_commit TRUNK_RECORDER_COMMIT "$TRUNK_RECORDER_COMMIT"
require_commit CALLSTREAM_COMMIT "$CALLSTREAM_COMMIT"
verify_patch trunk-recorder "$TRUNK_RECORDER_PATCH" "$TRUNK_RECORDER_PATCH_SHA256"

PREPARATION_SCHEMA=4
SOURCE_KEY="v${PREPARATION_SCHEMA}-${TRUNK_RECORDER_COMMIT:0:12}-${CALLSTREAM_COMMIT:0:12}-${TRUNK_RECORDER_PATCH_SHA256:0:12}"
SOURCE_PARENT="$SOURCE_ROOT/$SOURCE_KEY"
SOURCE_DIR="$SOURCE_PARENT/trunk-recorder"
PLUGIN_DIR="$SOURCE_DIR/user_plugins/callstream"
MARKER="$SOURCE_DIR/.pizzawave-native-source"

if [[ $PRINT_SOURCE_DIR -eq 1 ]]; then
    printf '%s\n' "$SOURCE_DIR"
    exit 0
fi

expected_marker_header() {
    cat <<EOF
preparation_schema=$PREPARATION_SCHEMA
trunk_recorder_commit=$TRUNK_RECORDER_COMMIT
trunk_recorder_patch_sha256=$TRUNK_RECORDER_PATCH_SHA256
callstream_commit=$CALLSTREAM_COMMIT
EOF
}

if [[ -e "$SOURCE_DIR" ]]; then
    if [[ ! -f "$MARKER" ]] || ! diff -u <(expected_marker_header) <(head -n 4 "$MARKER") >/dev/null; then
        echo "Error: managed source directory exists without the expected provenance marker: $SOURCE_DIR" >&2
        echo "Move it aside and retry; PizzaWave will not overwrite an uncertain source tree." >&2
        exit 1
    fi
    expected_tr_diff="$(awk -F= '$1 == "trunk_recorder_diff_sha256" {print $2}' "$MARKER")"
    expected_callstream_diff="$(awk -F= '$1 == "callstream_diff_sha256" {print $2}' "$MARKER")"
    actual_tr_diff="$(git -C "$SOURCE_DIR" diff --cached --binary HEAD | sha256sum | awk '{print $1}')"
    actual_callstream_diff="$(git -C "$PLUGIN_DIR" diff --cached --binary HEAD | sha256sum | awk '{print $1}')"
    expected_source_provenance="$(awk -F= '$1 == "source_provenance_sha256" {print $2}' "$MARKER")"
    actual_source_provenance=""
    if [[ -f "$SOURCE_DIR/pizzawave-native-source-provenance.json" ]]; then
        actual_source_provenance="$(sha256sum "$SOURCE_DIR/pizzawave-native-source-provenance.json" | awk '{print $1}')"
    fi
    if [[ ! "$expected_tr_diff" =~ ^[0-9a-f]{64}$ || "$actual_tr_diff" != "$expected_tr_diff" ]]; then
        echo "Error: managed trunk-recorder source differs from its prepared patch state: $SOURCE_DIR" >&2
        exit 1
    fi
    if [[ ! "$expected_callstream_diff" =~ ^[0-9a-f]{64}$ || "$actual_callstream_diff" != "$expected_callstream_diff" ]]; then
        echo "Error: managed callstream source differs from its prepared patch state: $PLUGIN_DIR" >&2
        exit 1
    fi
    if [[ ! "$expected_source_provenance" =~ ^[0-9a-f]{64}$ || "$actual_source_provenance" != "$expected_source_provenance" ]]; then
        echo "Error: managed source provenance record has changed: $SOURCE_DIR/pizzawave-native-source-provenance.json" >&2
        exit 1
    fi
    if ! git -C "$SOURCE_DIR" diff --quiet || ! git -C "$PLUGIN_DIR" diff --quiet; then
        echo "Error: managed native source contains unstaged changes." >&2
        exit 1
    fi
    unexpected_untracked="$(git -C "$SOURCE_DIR" status --porcelain --untracked-files=all | awk '$1 == "??" && $2 != ".pizzawave-native-source" && $2 != "pizzawave-native-source-provenance.json" && $2 !~ /^user_plugins\/callstream\// {print}')"
    if [[ -n "$unexpected_untracked" ]] || [[ -n "$(git -C "$PLUGIN_DIR" status --porcelain --untracked-files=all | awk '$1 == "??" {print}')" ]]; then
        echo "Error: managed native source contains unexpected untracked files." >&2
        exit 1
    fi
    git -C "$SOURCE_DIR" apply --reverse --check "$SCRIPT_DIR/$TRUNK_RECORDER_PATCH"
    printf 'Verified pinned native source tree: %s\n' "$SOURCE_DIR"
    exit 0
fi
if [[ -e "$SOURCE_PARENT" ]]; then
    echo "Error: managed source parent exists without a prepared source tree: $SOURCE_PARENT" >&2
    exit 1
fi

mkdir -p "$SOURCE_ROOT"
PREPARE_PARENT="$(mktemp -d "$SOURCE_ROOT/.prepare.${SOURCE_KEY}.XXXXXXXX")"
cleanup_prepare_dir() {
    rm -rf -- "$PREPARE_PARENT"
}
trap cleanup_prepare_dir EXIT
SOURCE_DIR="$PREPARE_PARENT/trunk-recorder"
PLUGIN_DIR="$SOURCE_DIR/user_plugins/callstream"
MARKER="$SOURCE_DIR/.pizzawave-native-source"

git clone --filter=blob:none --no-checkout "$TRUNK_RECORDER_REPOSITORY" "$SOURCE_DIR"
git -C "$SOURCE_DIR" checkout --detach "$TRUNK_RECORDER_COMMIT"
if [[ "$(git -C "$SOURCE_DIR" rev-parse HEAD)" != "$TRUNK_RECORDER_COMMIT" ]]; then
    echo "Error: trunk-recorder checkout did not resolve to its locked commit." >&2
    exit 1
fi

mkdir -p "$SOURCE_DIR/user_plugins"
git clone --filter=blob:none --no-checkout "$CALLSTREAM_REPOSITORY" "$PLUGIN_DIR"
git -C "$PLUGIN_DIR" checkout --detach "$CALLSTREAM_COMMIT"
if [[ "$(git -C "$PLUGIN_DIR" rev-parse HEAD)" != "$CALLSTREAM_COMMIT" ]]; then
    echo "Error: callstream checkout did not resolve to its locked commit." >&2
    exit 1
fi

git -C "$SOURCE_DIR" apply --check "$SCRIPT_DIR/$TRUNK_RECORDER_PATCH"
git -C "$SOURCE_DIR" apply "$SCRIPT_DIR/$TRUNK_RECORDER_PATCH"
git -C "$SOURCE_DIR" add --all
git -C "$PLUGIN_DIR" add --all
git -C "$SOURCE_DIR" diff --cached --check
git -C "$PLUGIN_DIR" diff --cached --check
cat > "$SOURCE_DIR/pizzawave-native-source-provenance.json" <<EOF
{
  "trunkRecorder": {
    "repository": "$TRUNK_RECORDER_REPOSITORY",
    "commit": "$TRUNK_RECORDER_COMMIT",
    "patch": "$TRUNK_RECORDER_PATCH",
    "patchSha256": "$TRUNK_RECORDER_PATCH_SHA256"
  },
  "callstream": {
    "repository": "$CALLSTREAM_REPOSITORY",
    "commit": "$CALLSTREAM_COMMIT",
    "patch": null,
    "patchSha256": null
  }
}
EOF

{
    expected_marker_header
    printf 'trunk_recorder_diff_sha256=%s\n' "$(git -C "$SOURCE_DIR" diff --cached --binary HEAD | sha256sum | awk '{print $1}')"
    printf 'callstream_diff_sha256=%s\n' "$(git -C "$PLUGIN_DIR" diff --cached --binary HEAD | sha256sum | awk '{print $1}')"
    printf 'source_provenance_sha256=%s\n' "$(sha256sum "$SOURCE_DIR/pizzawave-native-source-provenance.json" | awk '{print $1}')"
} > "$MARKER"

mv "$PREPARE_PARENT" "$SOURCE_PARENT"
trap - EXIT
printf 'Prepared pinned native source tree: %s\n' "$SOURCE_PARENT/trunk-recorder"
