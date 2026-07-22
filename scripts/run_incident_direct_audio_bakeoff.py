#!/usr/bin/env python3
"""Run a small, development-only direct-audio transcription/grounding bakeoff."""

from __future__ import annotations

import argparse
import hashlib
import json
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--audio", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model", default="mistralai/Voxtral-Mini-3B-2507")
    parser.add_argument("--max-transcription-tokens", type=int, default=256)
    parser.add_argument("--max-grounding-tokens", type=int, default=512)
    parser.add_argument("--transcription-only", action="store_true")
    return parser.parse_args()


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def generate(model: Any, processor: Any, inputs: Any, max_new_tokens: int) -> tuple[str, int]:
    import torch

    torch.cuda.synchronize()
    started = time.perf_counter()
    outputs = model.generate(**inputs, max_new_tokens=max_new_tokens, do_sample=False)
    torch.cuda.synchronize()
    elapsed_ms = round((time.perf_counter() - started) * 1000)
    decoded = processor.batch_decode(
        outputs[:, inputs.input_ids.shape[1] :],
        skip_special_tokens=True,
    )[0]
    return decoded, elapsed_ms


def main() -> int:
    args = parse_args()
    audio_paths = [path.resolve() for path in args.audio]
    for path in audio_paths:
        if "heldout-sealed" in str(path).lower():
            raise ValueError("the direct-audio bakeoff refuses sealed held-out paths")
        if not path.is_file():
            raise FileNotFoundError(path)

    import torch
    import transformers
    from transformers import AutoProcessor, VoxtralForConditionalGeneration

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required for this bakeoff")

    started_at = datetime.now(timezone.utc)
    load_started = time.perf_counter()
    processor = AutoProcessor.from_pretrained(args.model)
    model = VoxtralForConditionalGeneration.from_pretrained(
        args.model,
        torch_dtype=torch.bfloat16,
        device_map="cuda",
    )
    load_ms = round((time.perf_counter() - load_started) * 1000)

    grounding_prompt = (
        "Listen to this radio-call audio itself. Return a JSON object with keys "
        "heard_content, alternative_hearings, and unresolved_questions. "
        "Do not decide whether an event or incident exists. Do not assign a category, "
        "role, severity, or operational meaning. Do not infer an action, relationship, "
        "name, spelling, or location that is not directly supported by the audio. "
        "When speech is unclear, preserve plausible alternatives and uncertainty instead of guessing."
    )
    artifact: dict[str, Any] = {
        "started_at_utc": started_at.isoformat(),
        "model": args.model,
        "torch_version": torch.__version__,
        "transformers_version": transformers.__version__,
        "gpu": torch.cuda.get_device_name(0),
        "model_load_milliseconds": load_ms,
        "grounding_prompt": grounding_prompt,
        "results": [],
    }

    output_path = args.output.resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(artifact, indent=2), encoding="utf-8")

    for audio_path in audio_paths:
        torch.cuda.reset_peak_memory_stats()
        transcription_inputs = processor.apply_transcription_request(
            language="en",
            audio=str(audio_path),
            model_id=args.model,
        ).to("cuda", dtype=torch.bfloat16)
        transcript, transcription_ms = generate(
            model,
            processor,
            transcription_inputs,
            args.max_transcription_tokens,
        )

        grounding = None
        grounding_ms = None
        if not args.transcription_only:
            conversation = [
                {
                    "role": "user",
                    "content": [
                        {"type": "audio", "path": str(audio_path)},
                        {"type": "text", "text": grounding_prompt},
                    ],
                }
            ]
            grounding_inputs = processor.apply_chat_template(conversation).to(
                "cuda", dtype=torch.bfloat16
            )
            grounding, grounding_ms = generate(
                model,
                processor,
                grounding_inputs,
                args.max_grounding_tokens,
            )
        result = {
            "audio_path": str(audio_path),
            "audio_sha256": sha256_file(audio_path),
            "transcription": transcript,
            "transcription_milliseconds": transcription_ms,
            "grounding_response": grounding,
            "grounding_milliseconds": grounding_ms,
            "peak_allocated_bytes": torch.cuda.max_memory_allocated(),
            "peak_reserved_bytes": torch.cuda.max_memory_reserved(),
        }
        artifact["results"].append(result)
        output_path.write_text(json.dumps(artifact, indent=2), encoding="utf-8")
        print(json.dumps(result))

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
