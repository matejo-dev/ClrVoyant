// Integration tests launch real processes and the netcoredbg locator tests
// mutate a process-global env var; run all tests serially to avoid races.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
