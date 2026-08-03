# Native Trunk Recorder and Callstream builds

PizzaWave source builds use one exact Trunk Recorder commit, one exact
Callstream commit, and one temporary Trunk Recorder compatibility patch. The
commits and SHA-256 patch hash are recorded in `scripts/native-dependencies.lock`.
Moving branches are not build inputs.

`scripts/prepare_trunk_recorder_source.sh` verifies the lock and patch hashes,
checks out both commits in a managed source directory, applies the patches, and
refuses to reuse a source tree whose prepared content has changed. The Setup
`tr-source-build` action explicitly requests `--build`; ordinary PizzaWave
deployments copy the helper files but do not run them, replace native binaries,
or restart Trunk Recorder.

The explicit build compiles the combined tree and runs its CTest suite before
installing either native artifact.

After a successful install, the source commits, patch hashes, and installed
binary hashes are recorded in both:

- `/usr/local/bin/trunk-recorder.provenance.json`
- `/usr/local/lib/trunk-recorder/libcallstream.so.provenance.json`

The two native components must be rebuilt and installed as a pair. Trunk
Recorder passes the C++ `Call_Data_t` structure by value to plugins, and the
upstream plugin interface has no versioned extension or ABI negotiation. Adding
a field can therefore change the plugin ABI even when the field is appended and
default-initialized. A previously built plugin is not compatible evidence.
The new field is the final struct member so existing member offsets are
preserved, but the changed struct size still requires a paired rebuild.

The temporary core patch exposes one factual boolean,
`started_from_update`, from Trunk Recorder's existing `was_update` state. It is
available in `Call_Data_t` and call JSON. Pinned Callstream commit `6a46546b`
consumes that field directly. It does not add an inferred "incomplete
transmission" timestamp because the ordered transmission list already contains
the first transmission time and the core cannot prove that audio was truncated.

The `Native dependency compatibility` workflow prepares a clean locked source
pair, builds core and plugin together, and runs the native tests. When upstream
commits containing the accepted changes are available, advance the locked
Trunk Recorder commit and remove the checked-in patch in the same change.

Callstream's build-tree module contains a temporary build-directory RUNPATH.
CMake rewrites that RUNPATH to `/usr/local/lib/trunk-recorder` during install.
Install the CMake output; never copy `build/.../libcallstream.so` directly. The
CI staged-install check enforces this distinction.

## Production audit on 2026-08-03

The audit was read-only. Neither native service was restarted.

| Host | Trunk Recorder | Callstream | Result |
|---|---|---|---|
| OT | Experimental `d64f6d3`; installed binary SHA-256 `79d345f86b6def123436e128a52e58b1cffcf7fef0f5f0cc781c99c326d906d4` | Detached `b11d1c57`; installed plugin SHA-256 `1a083e61f7995bacdd93eef1342d83b55ee657ccedcb50797bae79d9a367771f` | Coherent experimental pair, but not stock upstream. It includes active source-affinity and capture diagnostics. |
| RPI | Experimental `2f6ca268`; installed binary SHA-256 `85e0ee88a86fcbc1327f01953ff952bf2d0c6f7023ba977dfefed90eca29ca8b` | Installed plugin SHA-256 `14e35d6976c0adc6429df769cc92057a0dfd1e9903738287493afe7977f83652`; exact Git commit unrecoverable | Mixed lineage. The plugin was rebuilt later from a non-Git temporary snapshot. |

Neither host had a native provenance manifest. Both installed plugins retained
a temporary build-directory RUNPATH. These installations must not be described
as reproducible from the current Setup helper.
