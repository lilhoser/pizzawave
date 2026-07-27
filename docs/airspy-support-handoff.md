# Airspy Support Status

Date: 2026-07-09

This document is retained only as a historical marker. The old Airspy hand-off
plan has been completed or superseded by Site Setup and should not be used as
implementation guidance.

Current behavior:

- Site Setup owns SDR inventory, RF path state, waterfall, RF sweep, source
  planning, and TR config apply.
- Airspy Mini devices are detected through the Setup SDR inventory path.
- Airspy RF validation uses Airspy-aware capture handling, frequency units,
  sample-rate choices, and linearity-gain presentation.
- Source planning accepts typed SDR device metadata for Setup-owned config
  generation.
- First-run no longer plans SDR sources. It only prepares host prerequisites:
  trunk-recorder install/reuse, optional LM Link support, and optional native
  Qdrant support.

The remaining operator validation for Airspy is field validation on real
hardware, not a separate hand-off implementation track.
