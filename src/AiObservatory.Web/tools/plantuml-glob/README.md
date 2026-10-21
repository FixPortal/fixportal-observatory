# PlantUML file-glob adapter

This private development adapter replaces only plantuml-parser@0.4.0's two
`fastGlob.sync(globPattern)` calls. It uses pinned tinyglobby@0.2.17 instead of
the unpatched braces chain (GHSA-vfj7-8cjw-p6xm). No production dependency or
architecture assertion is removed.

The installed consumer accepts string/array patterns without glob options.
The adapter rejects unsupported API extensions, disables directory expansion
and retains relative/absolute output forms, including Windows cross-drive paths.
The real installed parser is exercised for file/wildcard/brace/array/exclusion,
missing/directory and callback cases in `src/plantuml-glob.test.ts`.

The direct file dependency and `$fast-glob` consumer override must stay together:
a consumer-only relative file override resolves relative to the consumer's
installed directory and can produce an invalid dependency graph.

On a parser upgrade, inspect its fast-glob call sites and rerun the compatibility
suite, fresh install, installed graph, both audits and the complete repository
gate. Remove this adapter/override when the upstream dependency graph is fixed.
This package is private and is not published to a registry.
