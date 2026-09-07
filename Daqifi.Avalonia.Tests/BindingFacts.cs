using System.Reflection;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Reads the checkout's own source files and states facts about the bindings in them.
///
/// <para>
/// None of the views in this repo declare an <c>x:DataType</c>, so their bindings are resolved by
/// reflection at runtime: a renamed, moved or deleted member fails <b>silently</b> — the control
/// renders blank — while every head still builds green. Nothing in the compiler or the build gate
/// can see that break, which is why these facts are asserted textually here instead.
/// </para>
///
/// <para>
/// <see cref="AssertBinds"/> and <see cref="AssertExposes"/> are meant to be used together: the
/// binding exists in the markup, and the member it names is readable on the type the markup will
/// meet at runtime. <see cref="AssertBinds"/> carries most of that weight, because it is the half
/// nothing else in the build can see: without it the markup can go on naming a member that no longer
/// exists while every compiler check, both heads and every other test still pass.
/// </para>
///
/// <para>
/// <see cref="AssertExposes"/> is the narrower of the two, because call sites pass the member name as
/// <c>nameof</c>: deleting or renaming the member then breaks the build, and the runtime assertion
/// never gets to run. What it still catches is the shape the compiler is content with and a binding
/// is not — a member that becomes a public field or a method, or a property that loses its getter,
/// all of which leave <c>GetProperty</c> returning null or a write-only property behind.
/// </para>
/// </summary>
internal static class BindingFacts
{
    /// <summary>Reads a file from the checkout, given its repo-relative path with '/' separators.</summary>
    internal static string Source(string repoRelativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), repoRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Asserts that a view contains the given binding text verbatim.</summary>
    internal static void AssertBinds(string repoRelativeViewPath, string expectedBinding) =>
        Assert.Contains(expectedBinding, Source(repoRelativeViewPath), StringComparison.Ordinal);

    /// <summary>
    /// Asserts the other half: a reflection binding resolves against the runtime type, so the
    /// member must be public and readable there — the half the compiler never checks.
    /// </summary>
    internal static void AssertExposes(Type dataContext, string memberName)
    {
        var property = dataContext.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.True(property.CanRead, $"{dataContext.Name}.{memberName} cannot be read by a binding.");
    }

    /// <summary>Walks up from the test binary to the checkout, identified by the solution file.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Daqifi.Avalonia.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
