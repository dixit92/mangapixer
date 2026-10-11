# Reader load harness (performance profiling)

Work in progress: a synthetic multi-reader load generator for a MangaPixer test instance.
It logs in, opens items, requests pages the way the web reader does (current page plus
the reader's prefetch pattern) and records per-request timings to CSV with a summary.

Use it only against an instance you run yourself on synthetic or test media.
