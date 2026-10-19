using Microsoft.VisualStudio.TestTools.UnitTesting;

// The tests build their own objects and share nothing — safe to run in parallel.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
