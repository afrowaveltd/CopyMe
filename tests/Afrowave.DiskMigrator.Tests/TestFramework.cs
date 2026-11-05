using System.Diagnostics;
using System.Reflection;

namespace Afrowave.DiskMigrator.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    public TestAttribute(string? description = null) => Description = description;

    public string? Description { get; }
}

public sealed class TestFailureException : Exception
{
    public TestFailureException(string message) : base(message)
    {
    }
}

/// <summary>
/// A deliberately tiny test runner. Keeping the test harness dependency-free means the
/// whole solution restores and builds without a single third-party package.
/// </summary>
public static class TestRunner
{
    public static int RunAll(string? nameFilter)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var tests = Discover(assembly).ToList();

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            tests = tests
                .Where(t => (t.Type.Name + "." + t.Name).Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        Console.WriteLine($"Afrowave DiskMigrator – test suite ({tests.Count} testů)");
        Console.WriteLine(new string('=', 72));

        int passed = 0;
        var failures = new List<(string Name, Exception Error)>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var test in tests)
        {
            string display = $"{test.Type.Name}.{test.Method.Name}";
            try
            {
                test.Method.Invoke(null, null);
                passed++;
                Console.WriteLine($"  PASS  {display}");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                failures.Add((display, ex.InnerException));
                Console.WriteLine($"  FAIL  {display}");
                Console.WriteLine($"        {ex.InnerException.GetType().Name}: {Shorten(ex.InnerException.Message)}");
            }
            catch (Exception ex)
            {
                failures.Add((display, ex));
                Console.WriteLine($"  FAIL  {display}");
                Console.WriteLine($"        {ex.GetType().Name}: {Shorten(ex.Message)}");
            }
        }

        stopwatch.Stop();

        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Výsledek: {passed} prošlo, {failures.Count} selhalo, čas {stopwatch.Elapsed.TotalSeconds:0.00} s");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Neúspěšné testy:");
            foreach (var (name, error) in failures)
            {
                Console.WriteLine($"  • {name}");
                Console.WriteLine($"      {error.GetType().Name}: {Shorten(error.Message, 400)}");
            }

            return failures.Count;
        }

        return 0;
    }

    private static IEnumerable<(Type Type, MethodInfo Method, string Name)> Discover(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            // Static classes are abstract+sealed in IL; only skip genuine abstract bases.
            if (type.IsAbstract && !type.IsSealed) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
                         .OrderBy(m => m.MetadataToken))
            {
                if (method.GetParameters().Length != 0) continue;
                yield return (type, method, method.Name);
            }
        }
    }

    private static string Shorten(string message, int limit = 220)
    {
        string single = message.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= limit ? single : single[..limit] + "…";
    }
}

public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new TestFailureException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailureException($"{message} (očekáváno: {expected}, skutečnost: {actual})");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual, string message)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new TestFailureException($"{message} (nečekaná hodnota: {actual})");
        }
    }

    public static void Contains(string expectedSubstring, string? actual, string message)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new TestFailureException($"{message} („{expectedSubstring}“ nebylo nalezeno v: {actual})");
        }
    }

    public static void Contains<T>(IEnumerable<T> collection, T expected, string message)
    {
        if (!collection.Contains(expected))
        {
            throw new TestFailureException($"{message} (kolekce neobsahuje {expected})");
        }
    }

    public static void Empty<T>(IEnumerable<T> collection, string message)
    {
        var list = collection.ToList();
        if (list.Count > 0)
        {
            throw new TestFailureException($"{message} (obsahuje: {string.Join(" | ", list.Take(8))})");
        }
    }

    public static void NotEmpty<T>(IEnumerable<T> collection, string message)
    {
        if (!collection.Any())
        {
            throw new TestFailureException(message);
        }
    }

    public static T Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new TestFailureException($"{message} (očekávána {typeof(T).Name}, skutečnost {ex.GetType().Name}: {ex.Message})");
        }

        throw new TestFailureException($"{message} (nebyla vyhozena žádná výjimka, očekávána {typeof(T).Name})");
    }
}
