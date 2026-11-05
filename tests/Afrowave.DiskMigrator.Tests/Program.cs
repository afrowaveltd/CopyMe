// Afrowave DiskMigrator - automated test suite.
//
// Usage:
//   dotnet run --project tests/Afrowave.DiskMigrator.Tests [filter]
//
// The filter is a case-insensitive substring of "ClassName.MethodName".
// Exit code is the number of failed tests, so CI fails when anything is broken.

using Afrowave.DiskMigrator.Tests;

return TestRunner.RunAll(args.Length > 0 ? args[0] : null);
