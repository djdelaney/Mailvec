"""publish-images.yml: a manual run must not build a commit CI has not passed.

The workflow's header promises that a release image is a build that passed CI
on main. The workflow_run trigger only fires on green CI, but a manual dispatch
had no such trigger behind it and built main's head as it stood.
"""
import pathlib
import re
import unittest

REPO = pathlib.Path(__file__).resolve().parents[2]
TEXT = (REPO / ".github" / "workflows" / "publish-images.yml").read_text()
BUILD = TEXT[TEXT.index("\n  build:"):TEXT.index("\n  promote:")]


class ManualRunGate(unittest.TestCase):
    def step_positions(self):
        gate = BUILD.index("name: Require green CI for a manual run")
        return gate, BUILD.index("docker/login-action"), BUILD.index("docker/build-push-action")

    def test_the_gate_runs_before_the_registry_login_and_the_build(self):
        gate, login, build = self.step_positions()
        self.assertLess(gate, login)
        self.assertLess(gate, build)

    def test_the_gate_applies_to_manual_runs_and_fails_closed(self):
        gate, login, _ = self.step_positions()
        step = BUILD[gate:login]
        self.assertIn("if: github.event_name == 'workflow_dispatch'", step)
        # A successful CI run for a PUSH of exactly this commit, nothing looser.
        self.assertRegex(step, r"workflows/ci\.yml/runs\?head_sha=\$sha&event=push&status=success")
        self.assertIn("set -euo pipefail", step)
        self.assertIn("exit 1", step)
        self.assertNotIn("continue-on-error", step)

    def test_the_workflow_may_read_actions_results(self):
        self.assertRegex(TEXT, r"(?m)^permissions:\n(?:  .*\n)*?  actions: read")

    def test_ci_is_the_workflow_file_the_gate_names(self):
        self.assertTrue((REPO / ".github" / "workflows" / "ci.yml").exists())
        self.assertRegex((REPO / ".github" / "workflows" / "ci.yml").read_text(), r"(?m)^  push:\n    branches: \[main\]")


if __name__ == "__main__":
    unittest.main()
