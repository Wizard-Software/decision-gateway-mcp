"""Tests for the workflow hardening check.

Run from the repository root with the interpreter of the virtual environment
that holds the pinned PyYAML:

    python -B -I scripts/workflow-check/test_check_workflows.py -v
"""

import importlib.util
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import time
import unittest

import yaml

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parent.parent
SCRIPT = HERE / "check_workflows.py"
POSITIVE_DIR = HERE / "samples" / "positive"
NEGATIVE_DIR = HERE / "samples" / "negative"

spec = importlib.util.spec_from_file_location("check_workflows", SCRIPT)
cw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cw)

POSITIVE_HEADER = (
    "on: push\n"
    "permissions:\n"
    "  contents: read\n"
    "jobs:\n"
    "  build:\n"
    "    runs-on: ubuntu-latest\n"
)

PINNED = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1"


def run_cli(*args):
    return subprocess.run(
        [sys.executable, "-B", "-I", str(SCRIPT), *map(str, args)],
        capture_output=True,
        text=True,
        stdin=subprocess.DEVNULL,
        timeout=60,
    )


def rules_of(text):
    return {v.rule for v in cw.check_text(text)}


class PositiveSamples(unittest.TestCase):
    def test_positive_dir_has_expected_count(self):
        self.assertEqual(5, len(list(POSITIVE_DIR.glob("*.yml"))))

    def test_each_positive_sample_has_no_violations(self):
        for f in sorted(POSITIVE_DIR.glob("*.yml")):
            with self.subTest(sample=f.name):
                text = f.read_text(encoding="utf-8")
                self.assertEqual([], cw.check_text(text, f.name))

    def test_repository_workflows_pass(self):
        self.assertEqual([], cw.check_paths([REPO / ".github" / "workflows"]))


class NegativeSamples(unittest.TestCase):
    def test_each_negative_sample_reports_exactly_expected_rules(self):
        for f in sorted(NEGATIVE_DIR.glob("*.yml")):
            with self.subTest(sample=f.name):
                text = f.read_text(encoding="utf-8")
                m = re.match(r"# expect: (WF[0-9]{3}(?:, WF[0-9]{3})*)\n", text)
                self.assertIsNotNone(m, "missing '# expect:' header")
                expected = set(m.group(1).split(", "))
                self.assertEqual(expected, {v.rule for v in cw.check_text(text, f.name)})

    def test_negative_dir_has_expected_count(self):
        self.assertEqual(41, len(list(NEGATIVE_DIR.glob("*.yml"))))

    def test_printf_samples_keep_their_bytes(self):
        tab = (NEGATIVE_DIR / "tab-indent.yml").read_bytes()
        self.assertIn(b"\t", tab)
        for name in ("pull-request-target-escaped.yml", "escaped-permission-value.yml"):
            with self.subTest(sample=name):
                self.assertIn(b"\\x", (NEGATIVE_DIR / name).read_bytes())

    def test_no_stray_pull_request_target_text(self):
        needle = "pull_request_target"
        for f in sorted(POSITIVE_DIR.glob("*.yml")):
            with self.subTest(sample=f.name):
                self.assertNotIn(needle, f.read_text(encoding="utf-8"))
        for f in sorted(NEGATIVE_DIR.glob("*.yml")):
            text = f.read_text(encoding="utf-8")
            header = text.split("\n", 1)[0]
            with self.subTest(sample=f.name):
                self.assertNotIn(needle, header)
                if needle in text:
                    self.assertEqual("# expect: WF002", header)


class Cli(unittest.TestCase):
    def test_exit_0_on_clean_dir(self):
        result = run_cli(POSITIVE_DIR)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("", result.stdout)

    def test_exit_1_and_annotation_on_violation(self):
        sample = (NEGATIVE_DIR / "tag-ref.yml").read_text(encoding="utf-8")
        with tempfile.TemporaryDirectory() as tmp:
            (pathlib.Path(tmp) / "ci.yml").write_text(sample, encoding="utf-8")
            result = run_cli(tmp)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertTrue(result.stdout.startswith("::error file="), result.stdout)
        self.assertIn("title=WF001", result.stdout)

    def test_exit_2_on_missing_path(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = run_cli(pathlib.Path(tmp) / "does-not-exist")
        self.assertEqual(2, result.returncode)

    def test_exit_2_on_empty_dir(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = run_cli(tmp)
        self.assertEqual(2, result.returncode)

    def test_exit_2_on_oversized_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            (pathlib.Path(tmp) / "big.yml").write_bytes(b"#" * (cw.MAX_BYTES + 1))
            result = run_cli(tmp)
        self.assertEqual(2, result.returncode)

    def test_exit_2_on_invalid_utf8(self):
        with tempfile.TemporaryDirectory() as tmp:
            (pathlib.Path(tmp) / "bad.yml").write_bytes(b"name: \xff\xfe\n")
            result = run_cli(tmp)
        self.assertEqual(2, result.returncode)

    def test_exit_2_on_symlink_entry(self):
        with tempfile.TemporaryDirectory() as tmp:
            target = POSITIVE_DIR / "current-ci-shape.yml"
            os.symlink(target, pathlib.Path(tmp) / "link.yml")
            result = run_cli(tmp)
        self.assertEqual(2, result.returncode)

    def test_exit_2_on_uppercase_extension(self):
        sample = (POSITIVE_DIR / "current-ci-shape.yml").read_text(encoding="utf-8")
        with tempfile.TemporaryDirectory() as tmp:
            (pathlib.Path(tmp) / "ci.YML").write_text(sample, encoding="utf-8")
            result = run_cli(tmp)
        self.assertEqual(2, result.returncode)


class Unit(unittest.TestCase):
    def test_uses_inside_run_block_is_ignored(self):
        text = (
            POSITIVE_HEADER
            + "    steps:\n"
            + "      - run: |\n"
            + "          echo hello\n"
            + "          uses: actions/checkout@v4\n"
        )
        self.assertEqual(set(), rules_of(text))

    def test_annotation_escaping(self):
        v = cw.Violation("a,b:c.yml", 3, "WF001", "100% bad\nline\rend")
        self.assertEqual(
            "::error file=a%2Cb%3Ac.yml,line=3,title=WF001::100%25 bad%0Aline%0Dend",
            cw.format_annotation(v),
        )
        self.assertEqual("a%25b%0D%0A:,", cw.escape_data("a%b\r\n:,"))
        self.assertEqual("a%25b%0D%0A%3A%2C", cw.escape_property("a%b\r\n:,"))

    def test_stderr_paths_escaped(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = run_cli(pathlib.Path(tmp) / "a::b")
        self.assertEqual(2, result.returncode)
        self.assertIn("%3A%3A", result.stderr)
        self.assertNotIn("::", result.stderr)

    def test_line_separators_split_like_yaml(self):
        for sep in ("\r", "\x85", "\u2028", "\u2029"):
            with self.subTest(sep=repr(sep)):
                text = (
                    POSITIVE_HEADER
                    + "    steps:\n      - name: x"
                    + sep
                    + "        uses: actions/checkout@v4\n"
                )
                self.assertIn("WF001", rules_of(text))

    def test_bom_is_accepted(self):
        text = (POSITIVE_DIR / "current-ci-shape.yml").read_text(encoding="utf-8")
        self.assertEqual([], cw.check_text("\ufeff" + text))

    def test_large_single_line_is_fast(self):
        text = POSITIVE_HEADER + "    steps:\n      - name: " + "a" * (1024 * 1024 - 200) + "\n"
        start = time.monotonic()
        self.assertEqual([], cw.check_text(text))
        self.assertLess(time.monotonic() - start, 10.0)

    def test_pyyaml_version_matches_requirements(self):
        req = (HERE / "requirements.txt").read_text(encoding="utf-8")
        m = re.match(r"PyYAML==([0-9][0-9.]*)", req)
        self.assertIsNotNone(m)
        self.assertEqual(m.group(1), yaml.__version__)

    def test_messages_have_no_internal_identifiers(self):
        pattern = re.compile(r"\b(ADR|ENF)-[0-9]+")
        for path in (SCRIPT, pathlib.Path(__file__)):
            with self.subTest(file=path.name):
                self.assertIsNone(pattern.search(path.read_text(encoding="utf-8")))

    def test_local_reusable_workflow_is_accepted(self):
        for target in ("./.github/workflows/x.yml", "./.github/workflows/y-1_2.yaml"):
            with self.subTest(target=target):
                text = (
                    "on: push\npermissions:\n  contents: read\n"
                    "jobs:\n  call:\n    uses: " + target + "\n"
                )
                self.assertEqual([], cw.check_text(text))
        for target in (
            "./.github/actions/x.yml",
            "./.github/workflows/sub/x.yml",
            "./.github/workflows/../x.yml",
            "./.github/workflows/x.yml@main",
        ):
            with self.subTest(rejected=target):
                text = (
                    "on: push\npermissions:\n  contents: read\n"
                    "jobs:\n  call:\n    uses: " + target + "\n"
                )
                self.assertEqual({"WF001"}, rules_of(text))

    def test_deep_nesting_is_wf005(self):
        text = "on: push\nx: " + "[" * 500 + "]" * 500 + "\n"
        self.assertEqual({"WF005"}, rules_of(text))

    def test_deep_block_nesting_is_wf005(self):
        lines = ["on: push"]
        for depth in range(100):
            lines.append("  " * depth + "k%d:" % depth)
        text = "\n".join(lines) + "\n"
        self.assertEqual({"WF005"}, rules_of(text))

    def test_cyclic_alias_is_wf005(self):
        text = "on: push\nx: &a {k: *a}\n"
        self.assertEqual({"WF005"}, rules_of(text))

    def test_anchor_without_alias_is_wf005(self):
        text = POSITIVE_HEADER + "    env: &shared {A: b}\n    steps:\n      - run: echo ok\n"
        self.assertEqual({"WF005"}, rules_of(text))

    def test_release_environment_expression_is_not_exempt(self):
        template = (
            "on:\n  push:\n    tags: ['v*']\n"
            "permissions:\n  contents: read\n"
            "jobs:\n  release:\n    runs-on: ubuntu-latest\n"
            "    environment: {env}\n"
            "    permissions:\n      contents: write\n"
            "    steps:\n      - uses: " + PINNED + "\n"
        )
        with self.subTest(environment="literal"):
            self.assertEqual(set(), rules_of(template.format(env="release")))
        with self.subTest(environment="mapping"):
            self.assertEqual(set(), rules_of(template.format(env="{name: release}")))
        with self.subTest(environment="expression"):
            self.assertEqual({"WF004"}, rules_of(template.format(env="${{ '' }}")))
        with self.subTest(environment="expression-in-mapping"):
            self.assertEqual(
                {"WF004"}, rules_of(template.format(env="{name: '${{ vars.E }}'}"))
            )

    def test_duplicate_on_is_compared_by_value(self):
        text = (
            'on: push\n"on": pull_request\n'
            "permissions:\n  contents: read\njobs: {}\n"
        )
        self.assertEqual({"WF005"}, rules_of(text))


if __name__ == "__main__":
    unittest.main()
