"""Tests for the S30 (size_outliers.py) and S41 (comment_density.py) ai-smells detectors.

Each case writes a source file into a pytest tmp_path, runs the detector as CI does
(--root/--files/--smell) and reads its JSON, so the CLI contract is what is pinned.
"""
import json
import pathlib
import subprocess
import sys

import pytest

SCRIPTS = pathlib.Path(__file__).resolve().parents[1] / "ai-smells" / "detectors" / "scripts"


def run(script, tmp_path, name, text):
    (tmp_path / name).write_text(text, encoding="utf-8")
    (tmp_path / "files.txt").write_text(name + "\n", encoding="utf-8")
    out = subprocess.run(
        [sys.executable, str(SCRIPTS / script), "--root", str(tmp_path), "--files", str(tmp_path / "files.txt"), "--smell", "X"],
        capture_output=True, text=True, check=True,
    )
    return json.loads(out.stdout)


def body(n):
    return "".join(f"  step{i}();\n" for i in range(n))


# --- S30 -------------------------------------------------------------------------------

@pytest.mark.parametrize(
    "signature",
    [
        "function f(a: number): number {",
        "export function f(a: number): Promise<{ x: number }> {",
        "export async function f(a: number): Promise<void> {",
        "export const f = (a: number): number => {",
        "const f = async (a: number) => {",
        "export const f = useCallback((a: number) => {",
    ],
)
def test_s30_sees_typed_and_arrow_ts_functions(tmp_path, signature):
    result = run("size_outliers.py", tmp_path, "a.ts", signature + "\n" + body(130) + "}\n")
    assert [h["line"] for h in result["hits"]] == [1], result


def test_s30_sees_arrow_wrapped_in_call_without_brace_on_signature_line(tmp_path):
    text = "const Lazy = lazy(() =>\n  import('x').then(({ A }) => ({\n" + body(130) + "  })),\n)\n"
    result = run("size_outliers.py", tmp_path, "a.tsx", text)
    assert [h["line"] for h in result["hits"]] == [1], result


def test_s30_short_arrow_and_one_liners_are_not_hits(tmp_path):
    text = "const f = (a) => a + 1;\nconst g = (a: number): number => {\n  return a;\n};\nconst h = foo(1, 2);\n"
    assert run("size_outliers.py", tmp_path, "a.ts", text)["hits"] == []


@pytest.mark.parametrize("literal", ['"}"', "'}'", "`}`", "// }", "/* } */"])
def test_s30_ignores_a_closing_brace_in_a_literal_or_comment(tmp_path, literal):
    text = "function big() {\n  const c = 1; " + literal + "\n" + body(130) + "}\n"
    if literal.startswith(("//", "/*")):
        text = "function big() {\n  " + literal + "\n" + body(130) + "}\n"
    hits = run("size_outliers.py", tmp_path, "a.ts", text)["hits"]
    assert [(h["line"], h["endLine"]) for h in hits] == [(1, 133)], hits


@pytest.mark.parametrize("literal", ['"{"', "'{'", "// {"])
def test_s30_ignores_an_opening_brace_in_a_literal_or_comment(tmp_path, literal):
    short = f"function a() {{\n  const c = {literal};\n}}\n" if not literal.startswith("//") else f"function a() {{\n  {literal}\n}}\n"
    text = short + "function b() {\n" + body(130) + "}\n"
    hits = run("size_outliers.py", tmp_path, "a.ts", text)["hits"]
    assert [h["text"].split(": ", 1)[1] for h in hits] == ["function b() {"], hits


# --- S41 -------------------------------------------------------------------------------

def test_s41_wrapped_star_args_lines_are_not_comments(tmp_path):
    text = "def f(\n" + "    *args,\n" * 12 + "):\n" + "    pass\n" * 8
    assert run("comment_density.py", tmp_path, "a.py", text)["hits"] == []


def test_s41_decrement_statements_are_not_comments(tmp_path):
    text = "void M() {\n" + "  --depth;\n" * 14 + "  Go();\n" * 8 + "}\n"
    assert run("comment_density.py", tmp_path, "a.cs", text)["hits"] == []


def test_s41_jsdoc_block_still_counts_as_comment(tmp_path):
    text = "/**\n" + " * doc line\n" * 18 + " */\n" + "const a = 1;\n" * 4
    assert len(run("comment_density.py", tmp_path, "a.ts", text)["hits"]) == 1
