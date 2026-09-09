"""Regression tests for assert_gate_coverage.py, one per checker fix.

Fixture-level tests drive check_file over a written workflow, so the assertion and
the line grammar it parses cannot drift apart; unit tests pin the small helpers
directly. check_file reports a breach by sys.exit, which pytest sees as SystemExit.
"""
import textwrap

import pytest

import assert_gate_coverage as gate


WORKFLOW = """\
name: CI
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo build
  ci-gate:
    if: always()
    needs: [build]
    runs-on: ubuntu-latest
    steps:
      - name: Fail if any upstream job did not succeed
        if: {condition}
        {continue_on_error}
        run: |
          echo "Upstream results"
          exit 1
"""


def write_workflow(tmp_path, condition, continue_on_error=None):
    body = WORKFLOW.format(
        condition=condition,
        continue_on_error=continue_on_error or "",
    )
    path = tmp_path / "ci.yml"
    path.write_text(textwrap.dedent(body), encoding="utf-8")
    return str(path)


def check(path):
    return gate.check_file(path, "ci-gate", set(), set())


def test_correct_gate_passes(tmp_path):
    condition = "contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled')"
    assert check(write_workflow(tmp_path, condition)) is True


# M33: a failure-only condition is skipped when an upstream job is cancelled, so the
# required check reports green over a dependency that did not pass.
def test_failure_only_condition_is_refused(tmp_path):
    with pytest.raises(SystemExit, match="does not react to both 'failure' and 'cancelled'"):
        check(write_workflow(tmp_path, "contains(needs.*.result, 'failure')"))


# The converse of M33: a cancelled-only condition is false when an upstream job
# FAILS, so the gate step is skipped and the required check reports success over a
# failed dependency.
def test_cancelled_only_condition_is_refused(tmp_path):
    with pytest.raises(SystemExit, match="does not react to both 'failure' and 'cancelled'"):
        check(write_workflow(tmp_path, "contains(needs.*.result, 'cancelled')"))


@pytest.mark.parametrize(
    "condition",
    [
        # An inequality against success is true for failure and cancelled alike.
        "needs.build.result != 'success'",
        # The bracket spellings name the same reference GitHub resolves by string key.
        "needs['build'].result != 'success'",
        'needs["build"].result != "success"',
    ],
)
def test_cancelled_aware_spellings_pass(tmp_path, condition):
    assert check(write_workflow(tmp_path, condition)) is True


def test_bracket_only_gate_is_not_read_as_unreferenced(tmp_path):
    # Before NEEDS_RESULT learnt the index spelling this exited with "has no step
    # whose `if:` references a needs.<job>.result" -- a false RED on a correct gate.
    condition = "needs['build'].result == 'failure' || needs['build'].result == 'cancelled'"
    assert check(write_workflow(tmp_path, condition)) is True


# M32: YAML lets a plain scalar begin on the line after its key, so the split form
# below IS `continue-on-error: true`. Reading only the header line vouched for the
# step as not tolerant -- fail-open on a neutered gate.
def test_split_continue_on_error_is_still_tolerant(tmp_path):
    with pytest.raises(SystemExit, match="continue-on-error"):
        condition = "contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled')"
        check(write_workflow(tmp_path, condition, "continue-on-error:\n          true"))


def test_block_scalar_continue_on_error_is_still_tolerant(tmp_path):
    with pytest.raises(SystemExit, match="continue-on-error"):
        condition = "contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled')"
        check(write_workflow(tmp_path, condition, "continue-on-error: >\n          true"))


def test_block_scalar_false_continue_on_error_passes(tmp_path):
    # The header-only read compared the literal `>` against ("false", "") and refused
    # even a false value -- a false RED on a step that genuinely can fail its job.
    condition = "contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled')"
    assert check(write_workflow(tmp_path, condition, "continue-on-error: >\n          false")) is True


# Capitalised False is the same YAML 1.1 boolean as false; reading it as truthy was
# a false RED on a correct gate.
def test_capitalised_false_continue_on_error_passes(tmp_path):
    condition = "contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled')"
    assert check(write_workflow(tmp_path, condition, "continue-on-error: False")) is True


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        ("False", "false"),
        ("FALSE", "false"),
        ("${{ Always() }}", "always()"),
        ("'always()'", "always()"),
        ("always() && x", "always()&&x"),
    ],
)
def test_normalise_condition_folds_case(value, expected):
    assert gate.normalise_condition(value) == expected


@pytest.mark.parametrize(
    ("condition", "expected"),
    [
        ("needs.build.result != 'success'", ["build"]),
        ("needs['build'].result != 'success'", ["build"]),
        ('needs["build"].result != "success"', ["build"]),
        ("contains(needs.*.result, 'failure')", ["*"]),
        ("needs.a.result == 'failure' || needs['b'].result == 'cancelled'", ["a", "b"]),
        ("always()", []),
    ],
)
def test_needs_result_ids_both_spellings(condition, expected):
    assert gate.needs_result_ids(condition) == expected


# A trailing redirection does not change whether the command fails; the compound
# forms used to fullmatch-reject it, a false RED on the house one-liner.
@pytest.mark.parametrize(
    "body",
    [
        'echo "failed"; exit 1 >&2',
        'echo "failed" && exit 1 2>/dev/null',
        'if [ -z "$x" ]; then exit 1 >&2; fi',
        'if [ -z "$x" ]; then echo "missing"; exit 1 >&2; fi',
        "exit 1 >&2",
    ],
)
def test_failing_forms_accept_trailing_redirection(body):
    assert gate.ends_non_zero(body)


@pytest.mark.parametrize(
    "body",
    [
        "exit 1 | true",
        "false || true",
        'echo "then exit 1"',
        "true",
    ],
)
def test_non_failing_forms_stay_rejected(body):
    assert not gate.ends_non_zero(body)


# Failure recognition moved to ACCEPTED_FAILING_FORMS; the old constants and the
# docstrings describing their design invited patches to dead expressions.
def test_dead_constants_are_gone():
    assert not hasattr(gate, "NONZERO_EXIT")
    assert not hasattr(gate, "COMMAND_BOUNDARY")
