using System.IO.Compression;
using System.Reflection;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Monta o app sem janela: serviços de verdade apontando para uma pasta temporária (configurações, índice de pastas e
/// favoritos), e painéis e janela principal por cima. Nunca toca em <c>%AppData%\DoraExplorer</c>.
/// Troca estados estáticos (<c>App.SettingsService</c> e o arquivo de favoritos), então quem usa precisa estar na coleção
/// "App statics", que não roda em paralelo.
/// </summary>
internal sealed class PaneTestHarness : IDisposable
{
    private static readonly PropertyInfo SettingsProperty = typeof(App).GetProperty(nameof(App.SettingsService))!;

    private readonly object? _previousSettings = SettingsProperty.GetValue(null);

    public PaneTestHarness()
    {
        // Ao lado do próprio teste (e não na pasta Temp do usuário, com milhares de itens): para abrir uma pasta o painel
        // lista cada pasta do caminho até ela, e listar a Temp inteira a cada teste custava dezenas de segundos.
        Root = Directory.CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "dora-ui-" + Guid.NewGuid().ToString("N"))).FullName;
        Data = Directory.CreateDirectory(Path.Combine(Root, "dados")).FullName;

        Settings = new AppSettingsService(Path.Combine(Root, "settings.json"));
        SettingsProperty.GetSetMethod(nonPublic: true)!.Invoke(null, [Settings]);
        FavoritesService.UseStore(Path.Combine(Root, "favorites.json"));

        ViewSettings = new ExplorerViewSettingsService(Settings);
        PaneSettings = new PaneDisplaySettingsService(Settings);
        RememberPath = new RememberPathSettingsService(Settings);
        Administrator = new AdministratorStartupService(Settings, Elevation);
        Index =new DirectoryIndexService(Path.Combine(Root, "index.db"));
        Files = new LocalFileSystemService(ViewSettings, Index);
        Tree = new NavigationTreeService(Index, ViewSettings, Archives);
        Health = new NetworkClientHealthService(NetworkFiles);
    }

    /// <summary>Pasta temporária de tudo; some no fim.</summary>
    public string Root { get; }

    /// <summary>Onde os testes criam pastas e arquivos.</summary>
    public string Data { get; }

    public AppSettingsService Settings { get; }

    public ExplorerViewSettingsService ViewSettings { get; }

    public PaneDisplaySettingsService PaneSettings { get; }

    public RememberPathSettingsService RememberPath { get; }

    /// <summary>Elevação de mentira: o aviso do Windows (UAC) nunca é acionado pelos testes.</summary>
    public FakeElevationService Elevation { get; } = new();

    public AdministratorStartupService Administrator { get; }

    public DirectoryIndexService Index { get; }

    public ArchiveService Archives { get; } = new();

    public NetworkSessionStore Sessions { get; } = new();

    public NetworkFileService NetworkFiles { get; } = new();

    public RemoteFileService Remote { get; } = new();

    public FileOperationService Operations { get; } = new();

    public LocalFileSystemService Files { get; }

    public NavigationTreeService Tree { get; }

    public NetworkClientHealthService Health { get; }

    public void Dispose()
    {
        FavoritesService.UseStore(null);
        SettingsProperty.GetSetMethod(nonPublic: true)!.Invoke(null, [_previousSettings]);
        Index.Dispose();
        SqliteConnection.ClearAllPools(); // senão o banco temporário fica preso e a pasta não é apagada
        SshTestHelpers.DeleteDirectory(Root);
    }

    /// <summary>Opções de painel só com as unidades: sem Favoritos, Lixeira nem Rede (que leriam o computador).</summary>
    public static PaneDisplayOptions BareOptions() => new()
    {
        ShowFavorites = false,
        ShowRecycleBin = false,
        ShowFtp = false,
        ShowSftp = false,
        ShowScp = false,
        ShowTftp = false,
        ShowSmb = false,
        ShowNfs = false
    };

    public string Folder(string name) => Directory.CreateDirectory(Path.Combine(Data, name)).FullName;

    public static string File(string folder, string name, string content = "conteudo")
    {
        var path = Path.Combine(folder, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Cria um .zip com os arquivos dados (nome dentro do zip -> texto).</summary>
    public string Zip(string name, params (string Entry, string Text)[] entries)
    {
        var path = Path.Combine(Data, name);
        using var stream = System.IO.File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }

        return path;
    }

    /// <summary>Cria um .gz solto (somente leitura no app) com o texto dado.</summary>
    public string Gzip(string name, string text)
    {
        var path = Path.Combine(Data, name);
        using var stream = System.IO.File.Create(path);
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        gzip.Write(System.Text.Encoding.UTF8.GetBytes(text));
        return path;
    }

    public FilePaneViewModel CreatePane(string initialPath, PaneDisplayOptions? options = null, Action? saveDisplayOptions = null) => new(
        Files,
        Tree,
        ViewSettings,
        Sessions,
        NetworkFiles,
        Remote,
        Operations,
        Archives,
        Index,
        Health,
        options ?? BareOptions(),
        saveDisplayOptions ?? (() => { }),
        initialPath);

    /// <summary>Janela principal com os dois painéis (sem Favoritos, Lixeira nem Rede) nas pastas dadas.</summary>
    public MainWindowViewModel CreateMain()
    {
        foreach (var options in new[] { PaneSettings.LeftPane, PaneSettings.RightPane })
        {
            options.ShowFavorites = false;
            options.ShowRecycleBin = false;
            options.ShowFtp = false;
            options.ShowSftp = false;
            options.ShowScp = false;
            options.ShowTftp = false;
            options.ShowSmb = false;
            options.ShowNfs = false;
        }

        return new MainWindowViewModel(
            new ThemeManager(Settings),
            ViewSettings,
            PaneSettings,
            RememberPath,
            Administrator,
            Files,
            Tree,
            Sessions,
            new NetworkServerService(new NetworkServerLogStore()),
            NetworkFiles,
            Remote,
            Operations,
            Archives,
            Index,
            new FileSearchService(Index),
            Health);
    }

    /// <summary>
    /// Espera a condição ficar verdadeira (até <paramref name="timeoutMs"/>). A árvore do painel acompanha o disco em segundo
    /// plano: logo depois de criar, apagar ou ordenar, o nó pode levar alguns instantes para refletir a mudança.
    /// </summary>
    public static bool Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Sleep(20);
        }

        return true;
    }

    /// <summary>Procura, na árvore já carregada do painel, o nó que aponta para o caminho (ignora maiúsculas).</summary>
    public static NavigationTreeNodeViewModel? Find(FilePaneViewModel pane, string path)
    {
        foreach (var root in pane.NavigationRoots)
        {
            var found = Find(root, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static NavigationTreeNodeViewModel? Find(NavigationTreeNodeViewModel node, string path)
    {
        if (string.Equals(node.TargetPath?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = Find(child, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
