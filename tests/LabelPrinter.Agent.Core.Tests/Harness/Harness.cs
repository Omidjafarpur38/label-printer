using System.Diagnostics;
using System.Reflection;

namespace LabelPrinter.Agent.Core.Tests.Harness;

/// <summary>Marks a public instance method as a test. A non-null Skip reports it as skipped without running it.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    public string? Skip { get; init; }
}

public sealed class CheckFailedException(string message) : Exception(message);

public static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition) throw new CheckFailedException(what);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailedException($"{what}: expected <{expected}>, got <{actual}>");
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a)) throw new CheckFailedException($"{what}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }

    public static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new CheckFailedException($"{what}: expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new CheckFailedException($"{what}: expected {typeof(T).Name}, nothing was thrown");
    }
}

/// <summary>
/// Minimal runner: no NuGet packages. Usage: dotnet run --project tests/LabelPrinter.Agent.Core.Tests [-- name-filter]
/// Exit code 0 only when nothing failed. Passed, failed and skipped are reported separately.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<TestAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Method, Attr: x.Attr!, Name: $"{x.Method.DeclaringType!.Name}.{x.Method.Name}"))
            .Where(x => filter is null || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        int passed = 0, failed = 0, skipped = 0;
        var clock = Stopwatch.StartNew();
        foreach (var (method, attr, name) in tests)
        {
            if (attr.Skip is not null)
            {
                skipped++;
                Console.WriteLine($"SKIP  {name} ({attr.Skip})");
                continue;
            }
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(method.DeclaringType!);
                method.Invoke(instance, null);
                passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                Console.WriteLine($"FAIL  {name}\n      {cause.GetType().Name}: {cause.Message}\n{cause.StackTrace}");
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }

        Console.WriteLine($"\nTotal {tests.Count}  Passed {passed}  Failed {failed}  Skipped {skipped}  ({clock.ElapsedMilliseconds} ms)");
        if (tests.Count == 0) { Console.WriteLine("No tests matched."); return 2; }
        return failed == 0 ? 0 : 1;
    }
}
