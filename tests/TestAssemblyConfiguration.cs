using Microsoft.VisualStudio.TestTools.UnitTesting;

// Classes may run concurrently; methods and data rows within a class stay sequential.
[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]
