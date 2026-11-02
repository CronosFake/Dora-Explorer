using System.Reflection;
using System.Xml.Linq;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// A versão do programa mora num lugar só, o <c>&lt;Version&gt;</c> de <c>DoraExplorer.App.csproj</c>. O executável (e a tela "Sobre", que
/// lê a versão informacional) informa essa mesma versão, e o README a cita: mudar a versão num lugar e esquecer o outro quebra aqui.
/// </summary>
public sealed class AppVersionTests
{
    private static string FindInRepository(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{relativePath} não encontrado acima de {AppContext.BaseDirectory}");
    }

    private static string ProjectVersion() =>
        XDocument.Load(FindInRepository(@"src\DoraExplorer.App\DoraExplorer.App.csproj"))
            .Descendants("Version").FirstOrDefault()?.Value.Trim() ?? string.Empty;

    [Fact]
    public void The_project_file_declares_the_version_in_the_major_dot_minor_format()
    {
        ProjectVersion().Should().MatchRegex(@"^\d+\.\d+(\.\d+)?$", "o programa precisa declarar <Version> no csproj, e é de lá que a versão sai");
    }

    [Fact]
    public void The_built_app_reports_the_version_declared_in_the_project_file()
    {
        var informational = typeof(FilePaneViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        // A versão informacional pode trazer "+" e o commit (é o que a tela "Sobre" corta antes de mostrar).
        informational.Split('+')[0].Should().Be(ProjectVersion());
    }

    [Fact]
    public void The_readme_states_the_current_version()
    {
        File.ReadAllText(FindInRepository("README.md")).Should().Contain($"Versão atual: **{ProjectVersion()}**");
    }
}
