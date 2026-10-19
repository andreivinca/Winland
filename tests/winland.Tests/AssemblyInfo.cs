using Microsoft.VisualStudio.TestTools.UnitTesting;

// The tests are pure functions over value types — safe to run in parallel.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
