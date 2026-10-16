# Local XTerm.NET engine fork

This project is based on [tomlm/XTerm.NET](https://github.com/tomlm/XTerm.NET), tag `3.0.0`,
commit `453ed957885f2e87c777e9b4f0b88a3582065eab` (MIT; see `LICENSE`).

The source is built as a project reference so fixes to the engine's cell-width tables, leading
Khün clusters, iTerm2 image sizing, and SGR overline are exercised by this repository's tests
instead of being approximated in the UI wrapper. The UI NuGet package bundles the matching
`XTerm.NET.dll`; it does not declare a dependency on the unpatched upstream 3.0.0 package.
Preserve upstream attribution and record future upstream merges here.
