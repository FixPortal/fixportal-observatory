"""S41: file-wide hyper-commenting. A source file whose comment lines exceed 45% of its
non-blank lines, with at least 20 non-blank lines, is a hit spanning the whole file
(line 1 .. endLine). Source extensions only: in Markdown the comment pattern reads
headings, bullets and `---` rules as comments."""
import argparse, json, pathlib, re

DIRECTIVE = r"(?:include|region|endregion|ifdef|ifndef|if|else|elif|endif|pragma|define|undef|error|warning|line)\b"
# '#' + directive or Rust attribute is code. C-family sources may put whitespace after the '#'; elsewhere
# '# define the thing' is prose.
# No SOURCE language comments with `--` or `<!--`, and a bare leading `*` is a comment only
# inside an open /* block (see comment_count): otherwise `*args,` and `--depth;` read as prose.
COMMENT = re.compile(rf"^\s*(//|#(?!{DIRECTIVE}|\[|!\[)|/\*)")
C_COMMENT = re.compile(rf"^\s*(//|#(?![ \t]*{DIRECTIVE}|\[|!\[)|/\*)")
C_FAMILY = (".c", ".cpp", ".cs")
SOURCE = (".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".ps1", ".psm1",
          ".go", ".java", ".rs", ".rb", ".kt", ".swift", ".cpp", ".c")
THRESHOLD, MIN_LINES = 0.45, 20

def split_lines(text):
    """Lines as git counts them: only \\n ends one (a CR before it is dropped). str.splitlines()
    also breaks on \\v, \\f, \\x1c-\\x1e, NEL, U+2028/9 and a lone CR, which would shift every
    reported line number away from the PR diff's."""
    lines = [l[:-1] if l.endswith("\r") else l for l in text.split("\n")]
    if lines and lines[-1] == "":
        lines.pop()
    return lines

def comment_count(lines, pattern, ps_blocks=False):
    """ps_blocks: PowerShell `<# ... #>` blocks (comment-based help) count line by line."""
    count, in_block, in_ps = 0, False, False
    for line in lines:
        if in_ps:
            count += 1
            in_ps = "#>" not in line
        elif ps_blocks and line.lstrip().startswith("<#"):
            close = line.find("#>")
            in_ps = close < 0
            # `<# note #> $x = Do-Work` is a line of code that opens with a comment.
            if close < 0 or not line[close + 2:].strip():
                count += 1
        elif in_block:
            count += 1
            in_block = "*/" not in line
        elif pattern.match(line):
            count += 1
            stripped = line.lstrip()
            in_block = stripped.startswith("/*") and "*/" not in stripped
    return count

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True); ap.add_argument("--files", required=True); ap.add_argument("--smell", required=True)
    a = ap.parse_args()
    root = pathlib.Path(a.root)
    hits, unread = [], []
    for rel in pathlib.Path(a.files).read_text(encoding="utf-8").splitlines():
        rel = rel.strip()
        if not rel or not rel.lower().endswith(SOURCE):
            continue
        path = root / rel
        if not path.is_file():
            continue  # a gitlink / submodule path lists as a directory
        try:
            text = path.read_bytes().decode("utf-8", errors="replace")
        except OSError:
            unread.append(rel)
            continue
        all_lines = split_lines(text)
        lines = [l for l in all_lines if l.strip()]
        if len(lines) < MIN_LINES:
            continue
        comment = C_COMMENT if rel.lower().endswith(C_FAMILY) else COMMENT
        ratio = comment_count(lines, comment, rel.lower().endswith((".ps1", ".psm1"))) / len(lines)
        if ratio > THRESHOLD:
            hits.append({"file": rel, "line": 1, "endLine": len(all_lines), "text": f"{ratio:.0%} of {len(lines)} non-blank lines are comments"})
    status, reason = ("not assessed", "could not read " + ", ".join(unread)) if unread else ("assessed", "")
    print(json.dumps({"status": status, "reason": reason, "hits": sorted(hits, key=lambda h: h["file"])}))

if __name__ == "__main__":
    main()
