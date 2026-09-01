using System.Reflection;
using System.Text;
using Xunit;

namespace PunchIO.ApiSurface.Tests;

/// <summary>
/// Guards the shipped public surface against accidental change.
/// </summary>
/// <remarks>
/// <para>
/// Each shipping assembly's public API is rendered as a sorted text listing and
/// compared against a baseline checked into the repository. Adding, removing or
/// changing anything public fails this test until the baseline is updated
/// deliberately, which makes the change visible in review rather than shipping
/// silently to customers who depend on it.
/// </para>
/// <para>
/// This does the job <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c> does. It is
/// hand-rolled because that analyzer's baseline files are produced by an IDE code
/// fix, and a baseline nobody can regenerate from the command line is a baseline
/// that rots.
/// </para>
/// </remarks>
public class ApiSurfaceTests
{
    /// <summary>
    /// Set to regenerate every baseline instead of comparing. Use deliberately,
    /// then read the diff before committing it.
    /// </summary>
    private const bool Regenerate = false;

    public static TheoryData<string> Assemblies =>
    [
        "PunchIO.Core",
        "PunchIO.Configuration",
        "PunchIO.Cobol",
    ];

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void ThePublicSurfaceMatchesItsBaseline(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        string actual = Render(assembly);

        string path = BaselinePath(assemblyName);

        if (Regenerate || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);

            Assert.Fail(
                $"Baseline for {assemblyName} was written to {path}. " +
                "Review it, commit it, and re-run.");
        }

        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");

        if (expected == actual) return;

        Assert.Fail(
            $"The public surface of {assemblyName} no longer matches its baseline.\n\n" +
            Describe(expected, actual) +
            $"\nIf the change is intended, update {Path.GetFileName(path)}.");
    }

    [Fact]
    public void EveryPublicTypeIsDocumented()
    {
        // The XML documentation file is what drives IntelliSense, which is the
        // first thing a prospective customer sees.
        foreach (string name in new[] { "PunchIO.Core", "PunchIO.Configuration", "PunchIO.Cobol" })
        {
            var assembly = Assembly.Load(name);
            string xml = Path.ChangeExtension(assembly.Location, ".xml");

            Assert.True(File.Exists(xml), $"{name} shipped without its documentation file");

            string content = File.ReadAllText(xml);

            foreach (var type in PublicTypes(assembly))
            {
                Assert.Contains(
                    $"\"T:{type.FullName!.Replace('+', '.')}\"",
                    content,
                    StringComparison.Ordinal);
            }
        }
    }

    // ---- rendering ---------------------------------------------------------

    private static string Render(Assembly assembly)
    {
        var lines = new List<string>();

        foreach (var type in PublicTypes(assembly))
        {
            lines.Add(Describe(type));

            foreach (var member in type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                            BindingFlags.DeclaredOnly)
                .Where(IsVisible))
            {
                lines.Add("    " + Describe(member));
            }
        }

        lines.Sort(StringComparer.Ordinal);

        var builder = new StringBuilder();
        foreach (string line in lines) builder.Append(line).Append('\n');

        return builder.ToString();
    }

    private static IEnumerable<Type> PublicTypes(Assembly assembly) =>
        assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal);

    private static bool IsVisible(MemberInfo member) => member switch
    {
        // Accessors and event plumbing are rendered through their property or
        // event, not twice.
        MethodInfo method => !method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal),
        ConstructorInfo constructor => constructor.IsPublic,
        FieldInfo field => field.IsPublic,
        PropertyInfo or EventInfo or Type => true,
        _ => false,
    };

    private static string Describe(Type type)
    {
        string kind = type.IsEnum ? "enum"
            : type.IsInterface ? "interface"
            : type.IsValueType ? "struct"
            : "class";

        return $"{kind} {type.FullName}";
    }

    private static string Describe(MemberInfo member) => member switch
    {
        PropertyInfo p =>
            $"{Name(p.PropertyType)} {p.Name} {{ " +
            $"{(p.GetGetMethod() is not null ? "get; " : "")}" +
            $"{(p.GetSetMethod() is not null ? "set; " : "")}}}",

        MethodInfo m =>
            $"{Name(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Describe))})",

        ConstructorInfo c =>
            $".ctor({string.Join(", ", c.GetParameters().Select(Describe))})",

        FieldInfo f => $"{Name(f.FieldType)} {f.Name}",

        Type t => $"nested {t.Name}",

        _ => member.Name,
    };

    private static string Describe(ParameterInfo parameter) =>
        $"{Name(parameter.ParameterType)} {parameter.Name}" +
        (parameter.HasDefaultValue ? " = default" : string.Empty);

    private static string Name(Type type)
    {
        if (!type.IsGenericType) return type.Name.TrimEnd('&');

        string bare = type.Name.Contains('`', StringComparison.Ordinal)
            ? type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]
            : type.Name;

        return $"{bare}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }

    private static string BaselinePath(string assemblyName) =>
        Path.Combine(AppContext.BaseDirectory, "baselines", $"{assemblyName}.txt");

    private static string Describe(string expected, string actual)
    {
        var before = expected.Split('\n').ToHashSet(StringComparer.Ordinal);
        var after = actual.Split('\n').ToHashSet(StringComparer.Ordinal);

        var removed = before.Except(after).Where(l => l.Length > 0).Order(StringComparer.Ordinal);
        var added = after.Except(before).Where(l => l.Length > 0).Order(StringComparer.Ordinal);

        var builder = new StringBuilder();

        foreach (string line in removed) builder.Append("  removed: ").Append(line).Append('\n');
        foreach (string line in added) builder.Append("  added:   ").Append(line).Append('\n');

        return builder.ToString();
    }
}
