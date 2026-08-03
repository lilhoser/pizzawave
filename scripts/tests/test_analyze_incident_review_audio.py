import array
import importlib.util
import math
import pathlib
import sys
import tempfile
import unittest
import json


SCRIPT = pathlib.Path(__file__).parents[1] / "analyze_incident_review_audio.py"
SPEC = importlib.util.spec_from_file_location("audio_analysis", SCRIPT)
audio_analysis = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = audio_analysis
SPEC.loader.exec_module(audio_analysis)


class AudioAnalysisTests(unittest.TestCase):
    def test_peak_normalize_caps_gain(self):
        result = audio_analysis.peak_normalize([1, -1], max_gain_db=20)
        self.assertEqual(list(result), [10, -10])

    def test_peak_normalize_reaches_target_without_clipping(self):
        result = audio_analysis.peak_normalize([1000, -2000])
        expected = round(audio_analysis.PCM_MAX * (10 ** (-3 / 20)))
        self.assertLessEqual(max(abs(x) for x in result), 32767)
        self.assertAlmostEqual(max(abs(x) for x in result), expected, delta=1)

    def test_measure_reports_silence(self):
        metrics = audio_analysis.measure(8000, array.array("h", [0] * 8000))
        self.assertEqual(metrics.duration_seconds, 1)
        self.assertEqual(metrics.near_silent_fraction, 1)
        self.assertEqual(metrics.active_seconds, 0)

    def test_speech_filter_preserves_length_and_bounds(self):
        samples = array.array("h", (round(2000 * math.sin(2 * math.pi * 1000 * i / 8000)) for i in range(8000)))
        result = audio_analysis.speech_band_normalize(8000, samples)
        self.assertEqual(len(result), len(samples))
        self.assertLessEqual(max(abs(x) for x in result), 32767)
        self.assertGreater(max(abs(x) for x in result), 2000)

    def test_listening_results_map_answers_to_source_rows(self):
        selected = [
            {
                "package_key": "pilot-001",
                "source_key": "source-01",
                "call_id": 42,
                "talkgroup": "Dispatch",
                "duration_seconds": 2.5,
                "human_marked_unintelligible": True,
            }
        ]
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            review = root / "review.json"
            review.write_text(
                json.dumps({"created_at_utc": "2026-07-29T00:00:00Z", "samples": {"sample-01": "none"}}),
                encoding="utf-8",
            )
            audio_analysis.write_listening_results(selected, review, root)
            result = json.loads((root / "listening-results.json").read_text(encoding="utf-8"))
            self.assertEqual(result["answer_counts"]["none"], 1)
            self.assertEqual(result["previously_unintelligible_answer_counts"]["none"], 1)
            self.assertEqual(result["mapped_answers"][0]["call_id"], 42)


if __name__ == "__main__":
    unittest.main()
