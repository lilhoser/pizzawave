#!/usr/bin/env python3
import argparse
import json
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="tiny")
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--compute-type", default="int8")
    parser.add_argument("--cpu-threads", type=int, default=2)
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--vad-filter", default="false")
    args = parser.parse_args()

    start = time.perf_counter()
    try:
        from faster_whisper import WhisperModel
        model = WhisperModel(
            args.model,
            device=args.device,
            compute_type=args.compute_type,
            cpu_threads=args.cpu_threads,
            num_workers=args.workers,
        )
        model_error = None
    except Exception as exc:
        model = None
        model_error = str(exc)
    model_load_seconds = time.perf_counter() - start

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request = {}
        try:
            request = json.loads(line)
            if request.get("command") == "shutdown":
                break
            if model is None:
                raise RuntimeError(model_error or "faster-whisper model failed to initialize")
            transcribe_start = time.perf_counter()
            segments, info = model.transcribe(
                request["audio_path"],
                language="en",
                beam_size=1,
                vad_filter=args.vad_filter.lower() == "true",
                condition_on_previous_text=False,
            )
            text = " ".join(segment.text.strip() for segment in segments).strip()
            response = {
                "id": request.get("id"),
                "text": text,
                "seconds": time.perf_counter() - transcribe_start,
                "model_load_seconds": model_load_seconds,
                "language": getattr(info, "language", None),
                "language_probability": getattr(info, "language_probability", None),
            }
            model_load_seconds = 0
        except Exception as exc:
            response = {
                "id": request.get("id"),
                "text": "",
                "error": str(exc),
                "model_load_seconds": model_load_seconds,
            }
            model_load_seconds = 0
        print(json.dumps(response, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    main()
