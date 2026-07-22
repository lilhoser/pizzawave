#!/usr/bin/env python3
"""Run a development-corpus ASR bakeoff without touching PizzaWave runtime state."""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


MODELS = {
    "whisper-large-v3-turbo": "openai/whisper-large-v3-turbo",
    "parakeet-tdt-0.6b-v3": "nvidia/parakeet-tdt-0.6b-v3",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--audio-root", type=Path, required=True)
    parser.add_argument("--engine", choices=sorted(MODELS), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--bundle-id", action="append", default=[])
    parser.add_argument("--observation-id", action="append", default=[])
    parser.add_argument("--limit", type=int)
    return parser.parse_args()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def select_observations(package: dict[str, Any], args: argparse.Namespace) -> list[dict[str, Any]]:
    requested_bundles = set(args.bundle_id)
    requested_observations = set(args.observation_id)
    known_bundles = {bundle["bundleId"] for bundle in package["bundles"]}
    missing_bundles = requested_bundles - known_bundles
    if missing_bundles:
        raise ValueError(f"unknown bundle ids: {sorted(missing_bundles)}")

    selected: list[dict[str, Any]] = []
    known_observations: set[str] = set()
    for bundle in package["bundles"]:
        for observation in bundle["observations"]:
            observation_id = observation["observationId"]
            known_observations.add(observation_id)
            if requested_bundles and bundle["bundleId"] not in requested_bundles:
                continue
            if requested_observations and observation_id not in requested_observations:
                continue
            selected.append({"bundle_id": bundle["bundleId"], **observation})

    missing_observations = requested_observations - known_observations
    if missing_observations:
        raise ValueError(f"unknown observation ids: {sorted(missing_observations)}")
    if args.limit is not None:
        if args.limit < 1:
            raise ValueError("limit must be positive")
        selected = selected[: args.limit]
    return selected


def load_audio(path: Path, expected_rate: int, torch: Any, torchaudio: Any, soundfile: Any) -> Any:
    samples, source_rate = soundfile.read(path, dtype="float32", always_2d=True)
    waveform = torch.from_numpy(samples).mean(dim=1)
    if source_rate != expected_rate:
        waveform = torchaudio.functional.resample(waveform, source_rate, expected_rate)
    return waveform.numpy()


def main() -> int:
    args = parse_args()
    package_path = args.package.resolve()
    if "heldout-sealed" in str(package_path).lower():
        raise ValueError("the ASR bakeoff runner refuses sealed held-out corpus paths")

    package = json.loads(package_path.read_text(encoding="utf-8-sig"))
    observations = select_observations(package, args)
    if not observations:
        raise ValueError("selection contains no observations")

    import soundfile
    import torch
    import torchaudio
    import transformers
    from transformers import AutoProcessor

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required for this Ventax bakeoff")

    model_id = MODELS[args.engine]
    started = datetime.now(timezone.utc)
    load_started = time.perf_counter()
    processor = AutoProcessor.from_pretrained(model_id)
    if args.engine == "whisper-large-v3-turbo":
        from transformers import AutoModelForSpeechSeq2Seq

        model = AutoModelForSpeechSeq2Seq.from_pretrained(
            model_id,
            dtype=torch.float16,
            low_cpu_mem_usage=True,
            use_safetensors=True,
        ).to("cuda:0")
    else:
        from transformers import AutoModelForTDT

        model = AutoModelForTDT.from_pretrained(
            model_id,
            dtype=torch.float16,
            low_cpu_mem_usage=True,
            use_safetensors=True,
        ).to("cuda:0")
    model.eval()
    load_seconds = time.perf_counter() - load_started

    results: list[dict[str, Any]] = []
    for index, observation in enumerate(observations):
        audio_path = (args.audio_root / observation["audioReference"]).resolve()
        result: dict[str, Any] = {
            "index": index,
            "bundle_id": observation["bundle_id"],
            "observation_id": observation["observationId"],
            "audio_reference": observation["audioReference"],
            "audio_duration_milliseconds": observation["audioDurationMilliseconds"],
            "audio_sha256": sha256_file(audio_path) if audio_path.is_file() else None,
            "elapsed_seconds": None,
            "transcript": None,
            "error": None,
        }
        call_started = time.perf_counter()
        try:
            if not audio_path.is_file():
                raise FileNotFoundError(audio_path)
            audio = load_audio(
                audio_path,
                processor.feature_extractor.sampling_rate,
                torch,
                torchaudio,
                soundfile,
            )
            inputs = processor(audio, sampling_rate=processor.feature_extractor.sampling_rate, return_tensors="pt")
            inputs = inputs.to(device="cuda:0", dtype=torch.float16)
            with torch.inference_mode():
                if args.engine == "whisper-large-v3-turbo":
                    generated = model.generate(
                        **inputs,
                        language="en",
                        task="transcribe",
                        # Whisper reserves decoder positions for language/task
                        # control tokens; keep the request below its 448-token
                        # target-position ceiling.
                        max_new_tokens=440,
                    )
                    transcript = processor.batch_decode(generated, skip_special_tokens=True)[0]
                else:
                    generated = model.generate(**inputs, return_dict_in_generate=True)
                    transcript = processor.decode(generated.sequences, skip_special_tokens=True)
            if isinstance(transcript, (list, tuple)):
                transcript = transcript[0] if len(transcript) == 1 else " ".join(transcript)
            result["transcript"] = str(transcript).strip()
        except Exception as exc:  # Preserve per-call failures and continue the corpus run.
            result["error"] = f"{type(exc).__name__}: {exc}"
        finally:
            result["elapsed_seconds"] = time.perf_counter() - call_started
            results.append(result)

    total_audio_seconds = sum(item["audio_duration_milliseconds"] for item in results) / 1000
    inference_seconds = sum(item["elapsed_seconds"] for item in results)
    completed = sum(1 for item in results if item["error"] is None)
    warm_successes = [item for item in results[1:] if item["error"] is None]
    warm_seconds = sum(item["elapsed_seconds"] for item in warm_successes)
    artifact = {
        "artifact_version": 1,
        "started_at_utc": started.isoformat(),
        "completed_at_utc": datetime.now(timezone.utc).isoformat(),
        "engine": args.engine,
        "model_id": model_id,
        "package_path": str(package_path),
        "package_sha256": sha256_file(package_path),
        "python_version": platform.python_version(),
        "torch_version": torch.__version__,
        "transformers_version": transformers.__version__,
        "cuda_version": torch.version.cuda,
        "gpu": torch.cuda.get_device_name(0),
        "model_load_seconds": load_seconds,
        "calls_selected": len(results),
        "calls_completed": completed,
        "failure_count": len(results) - completed,
        "total_audio_seconds": total_audio_seconds,
        "inference_seconds": inference_seconds,
        "average_seconds_per_call": inference_seconds / len(results),
        "warm_calls_per_minute": (
            60 * len(warm_successes) / warm_seconds if warm_seconds > 0 else None
        ),
        "real_time_factor": inference_seconds / total_audio_seconds if total_audio_seconds > 0 else None,
        "results": results,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(artifact, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({key: artifact[key] for key in (
        "engine",
        "model_load_seconds",
        "calls_selected",
        "calls_completed",
        "failure_count",
        "total_audio_seconds",
        "inference_seconds",
        "average_seconds_per_call",
        "warm_calls_per_minute",
        "real_time_factor",
    )}, indent=2))
    return 0 if artifact["failure_count"] == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
