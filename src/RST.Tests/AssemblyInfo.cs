// Several test classes mutate process-global statics via
// AppDataPaths.OverrideRootForTests (a single shared Root). xUnit runs test
// collections in parallel by default, which lets those classes clobber each
// other's Root mid-run. Disable collection parallelization so the
// AppData-redirecting tests are deterministic.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
