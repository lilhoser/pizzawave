import importlib.util
import json
import pathlib
import sys
import tempfile
import unittest


SCRIPT = pathlib.Path(__file__).parents[1] / "evaluate_transcript_sense_model.py"
SPEC = importlib.util.spec_from_file_location("transcript_sense", SCRIPT)
transcript_sense = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = transcript_sense
SPEC.loader.exec_module(transcript_sense)


class TranscriptSenseTests(unittest.TestCase):
    def test_request_contains_transcript_but_no_source_identity(self):
        request = transcript_sense.build_request("small-model", "Unit 12 en route")
        serialized = json.dumps(request)
        self.assertIn("Unit 12 en route", serialized)
        self.assertNotIn("source_key", serialized)
        self.assertNotIn("call_id", serialized)
        self.assertEqual(request["temperature"], 0)
        self.assertEqual(request["response_format"]["type"], "json_schema")

    def test_parse_result_accepts_a_valid_structured_answer(self):
        response = {
            "choices": [
                {
                    "message": {
                        "content": json.dumps(
                            {"decision": "context_only"}
                        )
                    }
                }
            ]
        }
        self.assertEqual(transcript_sense.parse_result(response)["decision"], "context_only")

    def test_parse_result_rejects_an_extra_field(self):
        response = {
            "choices": [
                {
                    "message": {
                        "content": json.dumps(
                            {"decision": "useful", "explanation": "not allowed"}
                        )
                    }
                }
            ]
        }
        with self.assertRaisesRegex(ValueError, "unexpected"):
            transcript_sense.parse_result(response)

    def test_load_rows_joins_human_review_without_exposing_identity_to_model(self):
        package = {
            "packages": [
                {
                    "package_key": "window-1",
                    "evidence": [
                        {
                            "source_key": "source-01",
                            "source_manifest": {"call_id": 42},
                            "metadata": {"talkgroupName": "Dispatch"},
                            "transcripts": [{"text": "Unit 12 en route"}],
                        }
                    ],
                }
            ]
        }
        review = {
            "packages": [
                {
                    "package_key": "window-1",
                    "incidents": [],
                    "source_review_choices": {"source-01": "unsure"},
                    "unintelligible_audio_source_keys": ["source-01"],
                    "materially_wrong_transcript_source_keys": [],
                }
            ]
        }
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            package_path = root / "package.json"
            review_path = root / "review.json"
            package_path.write_text(json.dumps(package), encoding="utf-8")
            review_path.write_text(json.dumps(review), encoding="utf-8")
            rows = transcript_sense.load_rows(package_path, review_path)
        self.assertEqual(rows[0]["call_id"], 42)
        self.assertEqual(rows[0]["review_choice"], "unsure")
        self.assertTrue(rows[0]["human_marked_unintelligible"])
        request = transcript_sense.build_request("small-model", rows[0]["transcript"])
        self.assertNotIn("42", json.dumps(request))
        self.assertNotIn("source-01", json.dumps(request))


if __name__ == "__main__":
    unittest.main()
