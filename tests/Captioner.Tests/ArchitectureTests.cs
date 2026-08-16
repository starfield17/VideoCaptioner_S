using System.Reflection;
using Captioner.Core;
using Captioner.Engine;
using Captioner.Infrastructure;

namespace Captioner.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Core_does_not_reference_engine_infrastructure_or_cli()
    {
        var references = ReferencedAssemblyNames(typeof(TimedAnchor).Assembly);

        Assert.DoesNotContain("Captioner.Engine", references);
        Assert.DoesNotContain("Captioner.Infrastructure", references);
        Assert.DoesNotContain("captioner", references);
        Assert.DoesNotContain("captioner-desktop", references);
    }

    [Fact]
    public void Engine_references_core_but_not_infrastructure_or_frontends()
    {
        var references = ReferencedAssemblyNames(typeof(BatchRunner).Assembly);

        Assert.Contains("Captioner.Core", references);
        Assert.DoesNotContain("Captioner.Infrastructure", references);
        Assert.DoesNotContain("captioner", references);
        Assert.DoesNotContain("captioner-desktop", references);
    }

    [Fact]
    public void Infrastructure_references_ports_but_not_cli()
    {
        var references = ReferencedAssemblyNames(typeof(FileJobWorkspace).Assembly);

        Assert.Contains("Captioner.Core", references);
        Assert.Contains("Captioner.Engine", references);
        Assert.DoesNotContain("Captioner.Cli", references);
    }

    [Fact]
    public void Presentation_projects_share_the_engine_but_not_each_other()
    {
        var cli = ReferencedAssemblyNames(typeof(Captioner.Cli.Program).Assembly);
        var desktop = ReferencedAssemblyNames(typeof(Captioner.Desktop.JobRow).Assembly);

        Assert.Contains("Captioner.Engine", cli);
        Assert.Contains("Captioner.Infrastructure", cli);
        Assert.DoesNotContain("captioner-desktop", cli);
        Assert.Contains("Captioner.Engine", desktop);
        Assert.Contains("Captioner.Infrastructure", desktop);
        Assert.DoesNotContain("captioner", desktop);
    }

    private static HashSet<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
}
