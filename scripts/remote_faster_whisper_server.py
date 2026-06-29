#!/usr/bin/env python3
"""OpenAI-compatible faster-whisper transcription server for PizzaWave."""

from __future__ import annotations

import argparse
import asyncio
import os
import tempfile
import time
from pathlib import Path
from typing import Optional

import uvicorn
from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from faster_whisper import WhisperModel


app = FastAPI(title="PizzaWave faster-whisper transcription server")
model_lock = asyncio.Lock()
state: dict[str, object] = {
    "model": None,
    "model_name": "",
    "device": "",
    "compute_type": "",
    "language": "en",
    "vad_filter": False,
    "beam_size": 5,
    "temperature": 0.0,
    "repetition_penalty": 1.0,
    "no_repeat_ngram_size": 0,
    "condition_on_previous_text": False,
    "compression_ratio_threshold": 2.4,
    "log_prob_threshold": -1.0,
    "no_speech_threshold": 0.6,
    "started_at": time.time(),
    "requests": 0,
    "failures": 0,
    "api_key": "",
    "max_upload_mb": 64,
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run a faster-whisper HTTP transcription server.")
    parser.add_argument("--host", default=os.environ.get("PIZZAWAVE_FW_HOST", "0.0.0.0"))
    parser.add_argument("--port", type=int, default=int(os.environ.get("PIZZAWAVE_FW_PORT", "9187")))
    parser.add_argument("--model", default=os.environ.get("PIZZAWAVE_FW_MODEL", "small"))
    parser.add_argument("--device", default=os.environ.get("PIZZAWAVE_FW_DEVICE", "cuda"))
    parser.add_argument("--compute-type", default=os.environ.get("PIZZAWAVE_FW_COMPUTE_TYPE", "float16"))
    parser.add_argument("--cpu-threads", type=int, default=int(os.environ.get("PIZZAWAVE_FW_CPU_THREADS", "4")))
    parser.add_argument("--workers", type=int, default=int(os.environ.get("PIZZAWAVE_FW_WORKERS", "1")))
    parser.add_argument("--language", default=os.environ.get("PIZZAWAVE_FW_LANGUAGE", "en"))
    parser.add_argument("--vad-filter", action="store_true", default=os.environ.get("PIZZAWAVE_FW_VAD_FILTER", "").lower() == "true")
    parser.add_argument("--beam-size", type=int, default=int(os.environ.get("PIZZAWAVE_FW_BEAM_SIZE", "5")))
    parser.add_argument("--temperature", type=float, default=float(os.environ.get("PIZZAWAVE_FW_TEMPERATURE", "0")))
    parser.add_argument("--repetition-penalty", type=float, default=float(os.environ.get("PIZZAWAVE_FW_REPETITION_PENALTY", "1.0")))
    parser.add_argument("--no-repeat-ngram-size", type=int, default=int(os.environ.get("PIZZAWAVE_FW_NO_REPEAT_NGRAM_SIZE", "0")))
    parser.add_argument("--condition-on-previous-text", action="store_true", default=os.environ.get("PIZZAWAVE_FW_CONDITION_ON_PREVIOUS_TEXT", "").lower() == "true")
    parser.add_argument("--compression-ratio-threshold", type=float, default=float(os.environ.get("PIZZAWAVE_FW_COMPRESSION_RATIO_THRESHOLD", "2.4")))
    parser.add_argument("--log-prob-threshold", type=float, default=float(os.environ.get("PIZZAWAVE_FW_LOG_PROB_THRESHOLD", "-1.0")))
    parser.add_argument("--no-speech-threshold", type=float, default=float(os.environ.get("PIZZAWAVE_FW_NO_SPEECH_THRESHOLD", "0.6")))
    parser.add_argument("--api-key", default=os.environ.get("PIZZAWAVE_FW_API_KEY", os.environ.get("PIZZAWAVE_TRANSCRIBE_TOKEN", "")))
    parser.add_argument("--max-upload-mb", type=int, default=int(os.environ.get("PIZZAWAVE_FW_MAX_UPLOAD_MB", "64")))
    parser.add_argument("--log-level", default=os.environ.get("PIZZAWAVE_FW_LOG_LEVEL", "info"))
    return parser.parse_args()


def load_model(args: argparse.Namespace) -> None:
    started = time.perf_counter()
    model = WhisperModel(
        args.model,
        device=args.device,
        compute_type=args.compute_type,
        cpu_threads=max(1, args.cpu_threads),
        num_workers=max(1, args.workers),
    )
    state.update(
        {
            "model": model,
            "model_name": args.model,
            "device": args.device,
            "compute_type": args.compute_type,
            "language": args.language.strip() or None,
            "vad_filter": bool(args.vad_filter),
            "beam_size": max(1, args.beam_size),
            "temperature": args.temperature,
            "repetition_penalty": args.repetition_penalty,
            "no_repeat_ngram_size": max(0, args.no_repeat_ngram_size),
            "condition_on_previous_text": bool(args.condition_on_previous_text),
            "compression_ratio_threshold": args.compression_ratio_threshold,
            "log_prob_threshold": args.log_prob_threshold,
            "no_speech_threshold": args.no_speech_threshold,
            "model_load_seconds": round(time.perf_counter() - started, 3),
            "api_key": args.api_key.strip(),
            "max_upload_mb": max(1, args.max_upload_mb),
        }
    )


def require_auth(request: Request) -> None:
    api_key = str(state.get("api_key") or "")
    if not api_key:
        return
    header = request.headers.get("authorization", "")
    prefix = "Bearer "
    if not header.lower().startswith(prefix.lower()) or header[len(prefix) :].strip() != api_key:
        raise HTTPException(status_code=401, detail="missing or invalid bearer token")


@app.get("/health")
def health() -> dict[str, object]:
    return {
        "ok": state["model"] is not None,
        "model": state["model_name"],
        "device": state["device"],
        "compute_type": state["compute_type"],
        "language": state["language"],
        "vad_filter": state["vad_filter"],
        "beam_size": state["beam_size"],
        "temperature": state["temperature"],
        "repetition_penalty": state["repetition_penalty"],
        "no_repeat_ngram_size": state["no_repeat_ngram_size"],
        "condition_on_previous_text": state["condition_on_previous_text"],
        "compression_ratio_threshold": state["compression_ratio_threshold"],
        "log_prob_threshold": state["log_prob_threshold"],
        "no_speech_threshold": state["no_speech_threshold"],
        "uptime_seconds": round(time.time() - float(state["started_at"]), 1),
        "requests": state["requests"],
        "failures": state["failures"],
        "auth_required": bool(state.get("api_key")),
        "max_upload_mb": state["max_upload_mb"],
        "model_load_seconds": state.get("model_load_seconds", 0),
    }


@app.get("/v1/models")
def models() -> dict[str, object]:
    model_name = str(state["model_name"] or "faster-whisper")
    return {"object": "list", "data": [{"id": model_name, "object": "model", "owned_by": "pizzawave"}]}


@app.post("/v1/audio/transcriptions")
async def transcriptions(
    request: Request,
    file: UploadFile = File(...),
    model: Optional[str] = Form(default=None),
    language: Optional[str] = Form(default=None),
    response_format: Optional[str] = Form(default=None),
) -> dict[str, object] | str:
    require_auth(request)
    whisper_model = state.get("model")
    if whisper_model is None:
        raise HTTPException(status_code=503, detail="faster-whisper model is not loaded")

    suffix = Path(file.filename or "call.wav").suffix or ".wav"
    started = time.perf_counter()
    temp_path = ""
    try:
        with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as handle:
            temp_path = handle.name
            while True:
                chunk = await file.read(1024 * 1024)
                if not chunk:
                    break
                if handle.tell() + len(chunk) > int(state["max_upload_mb"]) * 1024 * 1024:
                    raise HTTPException(status_code=413, detail="uploaded audio exceeds configured size limit")
                handle.write(chunk)

        async with model_lock:
            segments, info = await asyncio.to_thread(
                whisper_model.transcribe,
                temp_path,
                language=language or state["language"] or None,
                beam_size=int(state["beam_size"]),
                temperature=float(state["temperature"]),
                repetition_penalty=float(state["repetition_penalty"]),
                no_repeat_ngram_size=int(state["no_repeat_ngram_size"]),
                condition_on_previous_text=bool(state["condition_on_previous_text"]),
                compression_ratio_threshold=float(state["compression_ratio_threshold"]),
                log_prob_threshold=float(state["log_prob_threshold"]),
                no_speech_threshold=float(state["no_speech_threshold"]),
                vad_filter=bool(state["vad_filter"]),
            )
            materialized_segments = list(segments)
            text = " ".join(segment.text.strip() for segment in materialized_segments).strip()

        elapsed = time.perf_counter() - started
        state["requests"] = int(state["requests"]) + 1
        duration = float(getattr(info, "duration", 0.0) or 0.0)
        if response_format == "text":
            return text
        return {
            "text": text,
            "model": model or state["model_name"],
            "duration": duration,
            "language": getattr(info, "language", None),
            "transcription_seconds": round(elapsed, 3),
            "realtime_factor": round(elapsed / duration, 4) if duration > 0 else None,
            "segments": len(materialized_segments),
            "avg_logprob": round(
                sum(float(getattr(segment, "avg_logprob", 0.0) or 0.0) for segment in materialized_segments) / len(materialized_segments),
                4,
            )
            if materialized_segments
            else None,
            "no_speech_prob": round(
                max(float(getattr(segment, "no_speech_prob", 0.0) or 0.0) for segment in materialized_segments),
                4,
            )
            if materialized_segments
            else None,
            "compression_ratio": round(
                max(float(getattr(segment, "compression_ratio", 0.0) or 0.0) for segment in materialized_segments),
                4,
            )
            if materialized_segments
            else None,
            "parameters": {
                "device": state["device"],
                "compute_type": state["compute_type"],
                "vad_filter": state["vad_filter"],
                "beam_size": state["beam_size"],
                "temperature": state["temperature"],
                "repetition_penalty": state["repetition_penalty"],
                "no_repeat_ngram_size": state["no_repeat_ngram_size"],
                "condition_on_previous_text": state["condition_on_previous_text"],
                "compression_ratio_threshold": state["compression_ratio_threshold"],
                "log_prob_threshold": state["log_prob_threshold"],
                "no_speech_threshold": state["no_speech_threshold"],
            },
        }
    except HTTPException:
        raise
    except Exception as exc:  # noqa: BLE001
        state["failures"] = int(state["failures"]) + 1
        raise HTTPException(status_code=500, detail=str(exc)) from exc
    finally:
        if temp_path:
            try:
                os.unlink(temp_path)
            except OSError:
                pass


def main() -> None:
    args = parse_args()
    load_model(args)
    uvicorn.run(app, host=args.host, port=args.port, log_level=args.log_level)


if __name__ == "__main__":
    main()
