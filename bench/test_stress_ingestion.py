import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import stress_ingestion as stress


class SaturationAssessmentTests(unittest.TestCase):
    def test_executable_reuse_requires_the_expected_revision_and_unchanged_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            executable = output / "engine"
            executable.write_bytes(b"published executable")
            binary = {"revision": "expected", "binary": str(executable),
                      "sha256": hashlib.sha256(executable.read_bytes()).hexdigest()}
            (output / "summary.json").write_text(json.dumps({"binaries": {"candidate": binary}}))
            self.assertEqual(binary, stress.reused_binary(output, "candidate", "expected"))
            with self.assertRaisesRegex(AssertionError, "revision"):
                stress.reused_binary(output, "candidate", "other")
            executable.write_bytes(b"changed executable")
            with self.assertRaisesRegex(AssertionError, "hash"):
                stress.reused_binary(output, "candidate", "expected")

    def setUp(self):
        self.level = {"eventsSent": 2000, "errors": 0, "ok": 2000, "throughput": 200,
                      "elapsedSeconds": 10, "visibilityTimeouts": 0, "visibilityDropped": 0, "p99": 50}
        self.counts = {"stored": 2000, "uniqueSessions": 2000}
        self.state = {"Running": True, "OOMKilled": False}

    def assess(self, drain=1, rate=0):
        return stress.classify(self.level, self.counts, self.state, drain, 10, rate)

    def test_a_drained_exact_zero_error_level_is_eligible_at_its_offered_rate(self):
        result = self.assess(rate=200)
        self.assertTrue(result["sustainable"])
        self.assertTrue(result["p99Under100ms"])

    def test_backpressure_oom_loss_duplicates_slow_drain_and_client_backlog_disqualify(self):
        mutations = [(self.level, "errors", 1), (self.level, "ok", 0), (self.level, "elapsedSeconds", 12),
                     (self.level, "visibilityTimeouts", 1), (self.level, "visibilityDropped", 1),
                     (self.counts, "stored", 1999), (self.counts, "stored", 2001),
                     (self.counts, "uniqueSessions", 1999), (self.state, "Running", False),
                     (self.state, "OOMKilled", True)]
        for target, key, value in mutations:
            with self.subTest(key=key, value=value):
                previous = target[key]
                target[key] = value
                self.assertFalse(self.assess()["sustainable"])
                target[key] = previous
        self.assertFalse(self.assess(drain=3.01)["sustainable"])
        self.assertFalse(self.assess(rate=205)["sustainable"])

    def test_partial_failed_beacon_prefixes_are_not_called_a_zero_error_integrity_pass(self):
        self.level["errors"] = 1
        self.counts.update(stored=2010, uniqueSessions=2010)
        result = self.assess()
        self.assertTrue(result["allAcknowledgedEventsCounted"])
        self.assertTrue(result["uniqueStoredSessions"])
        self.assertFalse(result["exactZeroErrorCounts"])
        self.assertFalse(result["sustainable"])

    def test_latency_budgets_are_distinct_from_the_throughput_ceiling(self):
        self.level["p99"] = 500
        result = self.assess()
        self.assertTrue(result["sustainable"])
        self.assertFalse(result["p99Under100ms"])
        self.assertTrue(result["p99Under1000ms"])

    def test_resource_cpu_window_excludes_later_drain_but_memory_peak_keeps_it(self):
        samples = [{"time": i, "cpuTicks": cpu, "rssBytes": rss, "cgroupBytes": rss + 1,
                    "throttledMicros": i * 10} for i, cpu, rss in [(0, 0, 100), (1, 100, 200), (2, 200, 300), (3, 200, 500)]]
        result = stress.resource_summary(samples, 2, 100)
        self.assertEqual(100, result["meanCpuPercentOfOneCore"])
        self.assertEqual(100, result["peakCpuPercentOfOneCore"])
        self.assertEqual(500, result["sampledPeakRssBytes"])

    def test_clickhouse_quoted_integer_results_are_normalized(self):
        with patch.object(stress.qualification, "docker", return_value='{"stored":"10","uniqueSessions":"9"}'):
            self.assertEqual({"stored": 10, "uniqueSessions": 9}, stress.stored_counts({"storage": "owned"}, "bench-track-c8-123"))
        with self.assertRaises(AssertionError):
            stress.stored_counts({"storage": "owned"}, "unsafe'query")

    def test_results_survive_a_later_overload_failure_and_unconfirmed_peaks_are_visible(self):
        valid = {"name": "baseline-track", "label": "baseline", "mode": "track", "stage": "ramp", "concurrency": 8,
                 "offeredRate": 0, "level": self.level | {"eventThroughput": 200, "p95": 25},
                 "counts": self.counts, "drainSeconds": 1, "assessment": self.assess()}
        failed = {"name": "overload", "label": "baseline", "mode": "track", "stage": "ramp", "concurrency": 1024,
                  "offeredRate": 0, "failure": "bounded client timeout"}
        summary = {"runs": [valid, failed]}
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            stress.save(output, summary)
            saved = json.loads((output / "summary.json").read_text())
            self.assertEqual(2, len(saved["runs"]))
            self.assertFalse(saved["ceilings"]["baseline/track"]["confirmationsPassed"])
            self.assertIn("failed", (output / "results.md").read_text())

    def test_a_repeated_lower_ceiling_can_pass_after_a_faster_short_ramp_fails_confirmation(self):
        def result(name, concurrency, rate, eligible):
            return {"name": name, "label": "baseline", "mode": "track", "stage": name, "concurrency": concurrency,
                    "level": {"eventThroughput": rate},
                    "assessment": {"sustainable": eligible, "p99Under100ms": eligible, "p99Under1000ms": eligible}}
        runs = [result("ramp", 16, 1000, True), result("confirm-1", 16, 900, False),
                result("confirm-1", 8, 750, True), result("confirm-2", 8, 770, True)]
        summary = stress.ceilings(runs)["baseline/track"]
        self.assertEqual("ramp", summary["sustainable"])
        self.assertTrue(summary["confirmationsPassed"])
        self.assertEqual({"concurrency": 8, "eventsPerSecond": 760}, summary["confirmed"])


if __name__ == "__main__":
    unittest.main()
