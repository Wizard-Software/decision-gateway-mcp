"""Workflow hardening check.

Parses GitHub Actions workflow files with the safe PyYAML loader and reports
violations of a small set of supply-chain rules as GitHub error annotations:

  WF001  every action reference is pinned to a full 40-character lowercase
         commit SHA, optionally followed by a "# vX.Y.Z" comment
  WF002  the "pull_request_target" trigger is not used
  WF003  the workflow-level permissions are exactly "contents: read"
  WF004  write access, "id-token", "attestations" and "read-all" appear only in
         a job named "release" of a workflow triggered solely by version tags
         that declares a literal environment name
  WF005  the file cannot be analysed safely (parse error, several documents,
         anchors, aliases, explicit tags, repeated keys, unexpected structure,
         excessive nesting) and is therefore rejected

Exit codes: 0 no violations, 1 violations found, 2 usage or read error.
"""

from __future__ import annotations

import bisect
import pathlib
import re
import sys
from typing import NamedTuple

MAX_BYTES = 1024 * 1024
MAX_DEPTH = 64

PR_TARGET = "pull_request_target"
STR_TAG = "tag:yaml.org,2002:str"

_LINE_BREAK = re.compile(r"\r\n|[\r\n\x85\u2028\u2029]")
_LINE_BREAK_CHAR = re.compile(r"[\r\n\x85\u2028\u2029]")
_REMOTE_USES = re.compile(
    r"[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*(?:/[A-Za-z0-9_.-]+)*@[0-9a-f]{40}"
)
_LOCAL_USES = re.compile(r"\./\.github/workflows/[A-Za-z0-9_][A-Za-z0-9_.-]*\.ya?ml")
_VERSION_COMMENT = re.compile(r"(?:[ ]+#[ ]v[0-9]+\.[0-9]+\.[0-9]+)?[ ]*")


class Violation(NamedTuple):
    path: str
    line: int
    rule: str
    message: str


class UsageError(Exception):
    """Invalid invocation or unreadable input; maps to exit code 2."""


def escape_property(value: str) -> str:
    """Escape a value used as a property of a workflow command."""
    return (
        value.replace("%", "%25")
        .replace("\r", "%0D")
        .replace("\n", "%0A")
        .replace(":", "%3A")
        .replace(",", "%2C")
    )


def escape_data(value: str) -> str:
    """Escape the message part of a workflow command."""
    return value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def format_annotation(v: Violation) -> str:
    return "::error file={},line={},title={}::{}".format(
        escape_property(v.path), v.line, escape_property(v.rule), escape_data(v.message)
    )


# --------------------------------------------------------------------------
# Text analysis
# --------------------------------------------------------------------------


class _Collector:
    def __init__(self, path: str) -> None:
        self.path = path
        self._seen: dict[tuple[int, str], Violation] = {}

    def add(self, line: int, rule: str, message: str) -> None:
        self._seen.setdefault((line, rule), Violation(self.path, line, rule, message))

    def result(self) -> list[Violation]:
        return [self._seen[k] for k in sorted(self._seen)]


def _line_starts(text: str) -> list[int]:
    starts = [0]
    starts.extend(m.end() for m in _LINE_BREAK.finditer(text))
    return starts


def _line_at(starts: list[int], index: int) -> int:
    return bisect.bisect_right(starts, index)


def _is_str(node, yaml) -> bool:
    return isinstance(node, yaml.ScalarNode) and node.tag == STR_TAG


def _get(mapping, name: str):
    """Return (key_node, value_node) of the first key with the given value."""
    for key, value in mapping.value:
        if key.value == name:
            return key, value
    return None, None


def _scan_events(text: str, yaml) -> tuple[int, str] | None:
    """Lazily walk the event stream. Return (line, message) of the first problem."""
    events = yaml.parse(text, Loader=yaml.SafeLoader)
    depth = 0
    try:
        for event in events:
            line = event.start_mark.line + 1
            if isinstance(event, yaml.AliasEvent):
                return line, "aliases are not allowed"
            if getattr(event, "anchor", None) is not None:
                return line, "anchors are not allowed"
            if getattr(event, "tag", None) is not None:
                return line, "explicit tags are not allowed"
            if isinstance(event, (yaml.MappingStartEvent, yaml.SequenceStartEvent)):
                depth += 1
                if depth > MAX_DEPTH:
                    return line, "nesting deeper than {} levels".format(MAX_DEPTH)
            elif isinstance(event, (yaml.MappingEndEvent, yaml.SequenceEndEvent)):
                depth -= 1
    finally:
        events.close()
    return None


def _walk_keys(root, yaml) -> tuple[int, str] | None:
    """Iteratively check every mapping for non-scalar and repeated keys."""
    stack = [root]
    while stack:
        node = stack.pop()
        if isinstance(node, yaml.MappingNode):
            seen: set[str] = set()
            for key, value in node.value:
                if not isinstance(key, yaml.ScalarNode):
                    return key.start_mark.line + 1, "mapping keys must be scalars"
                if key.value in seen:
                    return key.start_mark.line + 1, "repeated mapping key '{}'".format(key.value)
                seen.add(key.value)
                stack.append(value)
        elif isinstance(node, yaml.SequenceNode):
            stack.extend(node.value)
    return None


def _check_uses(key, value, parent, text: str, out: _Collector, yaml) -> None:
    line = value.start_mark.line + 1
    problem = None
    if not _is_str(value, yaml):
        problem = "the action reference must be a string"
    elif value.style not in (None, "'", '"'):
        problem = "the action reference must be a single-line plain or quoted string"
    elif value.start_mark.line != value.end_mark.line:
        problem = "the action reference must be on a single line"
    elif parent.flow_style is not False:
        problem = "step and job mappings that use an action must be in block style"
    else:
        ref = value.value
        if _LOCAL_USES.fullmatch(ref):
            pass
        elif _REMOTE_USES.fullmatch(ref) and not any(
            seg in (".", "..") for seg in ref.rsplit("@", 1)[0].split("/")
        ):
            pass
        else:
            problem = "the action reference must use the form owner/repo[/path]@<40-hex commit SHA>"
        if problem is None:
            end = value.end_mark.index
            m = _LINE_BREAK_CHAR.search(text, end)
            tail = text[end : m.start() if m else len(text)]
            if not _VERSION_COMMENT.fullmatch(tail):
                problem = "only a '# vX.Y.Z' comment may follow the commit SHA"
    if problem is not None:
        out.add(line, "WF001", problem)


def _event_names(on_value, yaml) -> list:
    """Scalar nodes naming the events of the top-level 'on' value."""
    if isinstance(on_value, yaml.ScalarNode):
        return [on_value]
    if isinstance(on_value, yaml.SequenceNode):
        return [n for n in on_value.value if isinstance(n, yaml.ScalarNode)]
    if isinstance(on_value, yaml.MappingNode):
        return [k for k, _ in on_value.value]
    return []


def _release_exempt(on_value, job_map, yaml) -> bool:
    """Conditions under which the job named 'release' may hold write access."""
    # (a) the only trigger is push
    if not isinstance(on_value, yaml.MappingNode) or len(on_value.value) != 1:
        return False
    key, push = on_value.value[0]
    if key.value != "push" or not isinstance(push, yaml.MappingNode) or len(push.value) != 1:
        return False
    # (b) push has only a tags filter
    key, tags = push.value[0]
    if key.value != "tags":
        return False
    # (c) tags is a non-empty list of patterns that start with 'v'
    if not isinstance(tags, yaml.SequenceNode) or not tags.value:
        return False
    for tag in tags.value:
        if not _is_str(tag, yaml) or not tag.value.startswith("v"):
            return False
    # (d) the job declares a literal, non-empty environment name
    _, env = _get(job_map, "environment")
    if env is None:
        return False
    if isinstance(env, yaml.MappingNode):
        _, env = _get(env, "name")
        if env is None:
            return False
    return _is_str(env, yaml) and env.value != "" and "${{" not in env.value


def _check_allowed_permissions(key, value, out: _Collector, yaml) -> None:
    line = key.start_mark.line + 1
    ok = isinstance(value, yaml.MappingNode)
    if ok:
        for k, v in value.value:
            allowed = ("none",) if k.value in ("id-token", "attestations") else ("read", "none")
            if not _is_str(v, yaml) or v.value not in allowed:
                ok = False
                break
    if not ok:
        out.add(
            line,
            "WF004",
            "permissions outside a release job may only be 'read' or 'none' "
            "(and 'none' for id-token and attestations)",
        )


def _check_tree(root, text: str, out: _Collector, yaml) -> None:
    if not isinstance(root, yaml.MappingNode):
        out.add(root.start_mark.line + 1, "WF005", "the workflow must be a mapping")
        return

    on_key, on_value = _get(root, "on")

    # WF002 in the parsed trigger list (catches quoted escape sequences).
    if on_value is not None:
        for name in _event_names(on_value, yaml):
            if PR_TARGET in name.value:
                out.add(name.start_mark.line + 1, "WF002", "the pull_request_target trigger is not allowed")

    # WF003 / WF004 at workflow level.
    perm_key, perm_value = _get(root, "permissions")
    if perm_key is None:
        out.add(1, "WF003", "workflow-level permissions must be exactly 'contents: read'")
    else:
        if not (
            isinstance(perm_value, yaml.MappingNode)
            and len(perm_value.value) == 1
            and perm_value.value[0][0].value == "contents"
            and _is_str(perm_value.value[0][1], yaml)
            and perm_value.value[0][1].value == "read"
        ):
            out.add(
                perm_key.start_mark.line + 1,
                "WF003",
                "workflow-level permissions must be exactly 'contents: read'",
            )
        _check_allowed_permissions(perm_key, perm_value, out, yaml)

    jobs_key, jobs = _get(root, "jobs")
    if jobs_key is None:
        return
    if not isinstance(jobs, yaml.MappingNode):
        out.add(jobs_key.start_mark.line + 1, "WF005", "'jobs' must be a mapping")
        return

    for job_id, job in jobs.value:
        if not isinstance(job, yaml.MappingNode):
            out.add(job_id.start_mark.line + 1, "WF005", "a job must be a mapping")
            continue

        use_key, use_value = _get(job, "uses")
        if use_key is not None:
            _check_uses(use_key, use_value, job, text, out, yaml)

        job_perm_key, job_perm = _get(job, "permissions")
        if job_perm_key is not None:
            exempt = job_id.value == "release" and _release_exempt(on_value, job, yaml)
            if not exempt:
                _check_allowed_permissions(job_perm_key, job_perm, out, yaml)

        steps_key, steps = _get(job, "steps")
        if steps_key is None:
            continue
        if not isinstance(steps, yaml.SequenceNode):
            out.add(steps_key.start_mark.line + 1, "WF005", "'steps' must be a list")
            continue
        for step in steps.value:
            if not isinstance(step, yaml.MappingNode):
                out.add(step.start_mark.line + 1, "WF005", "a step must be a mapping")
                continue
            key, value = _get(step, "uses")
            if key is not None:
                _check_uses(key, value, step, text, out, yaml)


def check_text(text: str, path: str = "<memory>") -> list[Violation]:
    import yaml

    out = _Collector(path)
    starts = _line_starts(text)

    # Stage 1: the forbidden trigger anywhere in the raw text.
    pos = text.find(PR_TARGET)
    while pos != -1:
        out.add(_line_at(starts, pos), "WF002", "the pull_request_target trigger is not allowed")
        pos = text.find(PR_TARGET, pos + len(PR_TARGET))

    # Stage 2: preconditions; any failure reports WF005 and skips the tree rules.
    problem = None
    root = None
    try:
        problem = _scan_events(text, yaml)
        if problem is None:
            documents = list(yaml.compose_all(text, Loader=yaml.SafeLoader))
            if len(documents) != 1:
                problem = (1, "the file must contain exactly one YAML document")
            else:
                root = documents[0]
                problem = _walk_keys(root, yaml)
    except yaml.YAMLError as exc:
        mark = getattr(exc, "problem_mark", None)
        problem = ((mark.line + 1) if mark is not None else 1, "the file cannot be parsed as YAML")
    except RecursionError:
        problem = (1, "the file is nested too deeply")
    if problem is not None:
        out.add(problem[0], "WF005", problem[1])
        return out.result()

    # Stages 3-6: rules on the node tree.
    try:
        _check_tree(root, text, out, yaml)
    except RecursionError:
        out.add(1, "WF005", "the file is nested too deeply")
    return out.result()


# --------------------------------------------------------------------------
# Files
# --------------------------------------------------------------------------


def _is_workflow_name(name: str) -> bool:
    return name.endswith((".yml", ".yaml"))


def iter_workflow_files(paths: list[pathlib.Path]) -> list[pathlib.Path]:
    found: list[pathlib.Path] = []
    for p in paths:
        shown = escape_property(str(p))
        if not p.exists() and not p.is_symlink():
            raise UsageError("path does not exist: " + shown)
        if p.is_dir():
            for entry in sorted(p.iterdir()):
                name = entry.name
                if not name.lower().endswith((".yml", ".yaml")):
                    continue
                entry_shown = escape_property(str(entry))
                if not _is_workflow_name(name):
                    raise UsageError("workflow extension must be lower case: " + entry_shown)
                if entry.is_symlink() or not entry.is_file():
                    raise UsageError("workflow entry is not a regular file: " + entry_shown)
                found.append(entry)
        else:
            if p.is_symlink() or not p.is_file():
                raise UsageError("not a regular file: " + shown)
            if not _is_workflow_name(p.name):
                raise UsageError("not a .yml or .yaml file: " + shown)
            found.append(p)
    if not found:
        raise UsageError("no workflow files found")
    return found


def read_workflow(path: pathlib.Path) -> str:
    shown = escape_property(str(path))
    try:
        with open(path, "rb") as handle:
            data = handle.read(MAX_BYTES + 1)
    except OSError as exc:
        raise UsageError("cannot read {}: {}".format(shown, escape_property(exc.strerror or "error"))) from None
    if len(data) > MAX_BYTES:
        raise UsageError("file is larger than {} bytes: {}".format(MAX_BYTES, shown))
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        raise UsageError("file is not valid UTF-8: " + shown) from None


def check_paths(paths: list[pathlib.Path]) -> list[Violation]:
    violations: list[Violation] = []
    for path in iter_workflow_files(paths):
        violations.extend(check_text(read_workflow(path), str(path)))
    return violations


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    try:
        import yaml  # noqa: F401
    except ImportError:
        print(
            "workflow-check: the yaml module is missing; install requirements.txt into a virtual environment",
            file=sys.stderr,
        )
        return 2
    paths = [pathlib.Path(a) for a in args] or [pathlib.Path(".github/workflows")]
    try:
        files = iter_workflow_files(paths)
        violations: list[Violation] = []
        for path in files:
            violations.extend(check_text(read_workflow(path), str(path)))
    except UsageError as exc:
        print("workflow-check: error: {}".format(exc), file=sys.stderr)
        return 2
    for v in violations:
        print(format_annotation(v))
    print(
        "workflow-check: {} file(s) checked, {} violation(s)".format(len(files), len(violations)),
        file=sys.stderr,
    )
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
