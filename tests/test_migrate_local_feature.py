#!/usr/bin/env python3
"""Exercise migrate-local-feature.sh without connecting to MySQL."""

from __future__ import annotations

import os
import stat
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "migrate-local-feature.sh"


MOCK_DOCKER = r"""#!/usr/bin/env python3
import os
import sys

mode = os.environ.get("MOCK_DOCKER_MODE", "success")
log_path = os.environ["MOCK_DOCKER_LOG"]
args = sys.argv[1:]
with open(log_path, "a", encoding="utf-8") as log:
    log.write("ARGS " + " ".join(args) + "\n")

if args[:1] == ["info"]:
    raise SystemExit(0)

if args[:2] == ["compose", "ps"]:
    if mode == "no_mysql":
        raise SystemExit(0)
    print("mock-mysql")
    raise SystemExit(0)

if args[:2] != ["compose", "exec"]:
    raise SystemExit(2)

payload = sys.stdin.read()
if "--execute" in args:
    with open(log_path, "a", encoding="utf-8") as log:
        log.write("HEALTH\n")
    if mode == "health_fail":
        raise SystemExit(1)
    raise SystemExit(0)

if "SELECT expected.table_name" in payload:
    with open(log_path, "a", encoding="utf-8") as log:
        log.write("PREFLIGHT\n")
    if mode == "prereq_missing":
        print("questions")
    raise SystemExit(0)

if "019_prompt_monitoring_credits.sql" in payload:
    with open(log_path, "a", encoding="utf-8") as log:
        log.write("MIGRATION_019\n")
    if mode == "migration_019_fail":
        raise SystemExit(1)
elif "020_prompt_groups.sql" in payload:
    with open(log_path, "a", encoding="utf-8") as log:
        log.write("MIGRATION_020\n")
raise SystemExit(0)
"""


class MigrationEntryPointTests(unittest.TestCase):
    def run_script(self, *args: str, mode: str = "success") -> tuple[subprocess.CompletedProcess[str], str]:
        with tempfile.TemporaryDirectory() as temp_dir:
            temp = Path(temp_dir)
            docker = temp / "docker"
            docker.write_text(textwrap.dedent(MOCK_DOCKER), encoding="utf-8")
            docker.chmod(docker.stat().st_mode | stat.S_IXUSR)
            log_path = temp / "docker.log"
            environment = os.environ.copy()
            environment["PATH"] = f"{temp}{os.pathsep}{environment['PATH']}"
            environment["MOCK_DOCKER_MODE"] = mode
            environment["MOCK_DOCKER_LOG"] = str(log_path)
            result = subprocess.run(
                [str(SCRIPT), *args],
                cwd=ROOT,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )
            return result, log_path.read_text(encoding="utf-8") if log_path.exists() else ""

    def test_success_runs_019_then_020(self) -> None:
        result, log = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertLess(log.index("HEALTH"), log.index("PREFLIGHT"))
        self.assertLess(log.index("PREFLIGHT"), log.index("MIGRATION_019"))
        self.assertLess(log.index("MIGRATION_019"), log.index("MIGRATION_020"))

    def test_missing_prerequisite_runs_no_migration(self) -> None:
        result, log = self.run_script(mode="prereq_missing")
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("MIGRATION_019", log)
        self.assertNotIn("MIGRATION_020", log)

    def test_failed_019_does_not_run_020(self) -> None:
        result, log = self.run_script(mode="migration_019_fail")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("MIGRATION_019", log)
        self.assertNotIn("MIGRATION_020", log)

    def test_unready_database_runs_no_migration(self) -> None:
        result, log = self.run_script(mode="health_fail")
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("MIGRATION_019", log)
        self.assertNotIn("MIGRATION_020", log)

    def test_stopped_mysql_runs_no_migration(self) -> None:
        result, log = self.run_script(mode="no_mysql")
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("MIGRATION_019", log)
        self.assertNotIn("MIGRATION_020", log)

    def test_help_does_not_call_docker(self) -> None:
        for argument in ("--help", "-h"):
            with self.subTest(argument=argument):
                result, log = self.run_script(argument)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("Usage:", result.stdout)
                self.assertEqual(log, "")

    def test_unknown_argument_fails_before_docker(self) -> None:
        result, log = self.run_script("--dry-run")
        self.assertEqual(result.returncode, 2)
        self.assertIn("Unexpected arguments", result.stderr)
        self.assertEqual(log, "")


if __name__ == "__main__":
    unittest.main()
