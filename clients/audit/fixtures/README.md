# Synthetic audit extraction fixtures

These invented Claude/Codex JSONL files contain no installation history. The deliberately planted private strings test that conversation fields never enter capture records, manifests, diagnostics, or chunks.

`capture-synthetic.mjs <new-output-directory>` captures only copies of these files in a disposable temporary directory. The .NET `NodeCaptureTests` runs it using Node 24, compares every emitted record and manifest with the independent canonical serializer, then validates the original chunks through the public bundle writer/reader. It does not calculate reference token totals in JavaScript or duplicate .NET accounting.

Native records without affirmative session closure remain unknown. A turn completion does not establish session closure. Real source/version and closure qualification belongs to the later release qualification task.
