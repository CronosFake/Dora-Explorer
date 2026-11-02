using System.Globalization;
using System.IO;
using System.Text.Json;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsFilePath;
    private readonly object _ioLock = new();

    public AppSettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DoraExplorer",
            "settings.json"))
    {
    }

    // Usado nos testes, para não tocar nas configurações reais do usuário.
    internal AppSettingsService(string settingsFilePath)
    {
        var directory = Path.GetDirectoryName(settingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _settingsFilePath = settingsFilePath;
    }

    /// <summary>
    /// Reescreve o arquivo de configurações se ainda houver senhas em texto puro (formato antigo):
    /// ao salvar, o conversor passa a protegê-las com DPAPI.
    /// </summary>
    public void ProtectLegacyPasswords()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_settingsFilePath));
            if (!ContainsPlainPassword(document.RootElement))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning($"Não foi possível verificar senhas em texto puro: {ex.Message}");
            return;
        }

        SaveSettings(LoadSettings());
        AppLogService.Info("Senhas guardadas em texto puro foram protegidas com DPAPI.");
    }

    private static bool ContainsPlainPassword(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property =>
                IsPlainPassword(property) || ContainsPlainPassword(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(ContainsPlainPassword),
            _ => false
        };

    private static bool IsPlainPassword(JsonProperty property) =>
        property.NameEquals("Password") &&
        property.Value.ValueKind == JsonValueKind.String &&
        property.Value.GetString() is { Length: > 0 } value &&
        !value.StartsWith(ProtectedStringJsonConverter.Prefix, StringComparison.Ordinal);

    public AppThemeMode LoadThemeMode() => LoadSettings().ThemeMode;

    public string LoadAccentColorHex() => LoadSettings().AccentColorHex;

    public ExplorerViewOptions LoadExplorerViewOptions() => LoadSettings().ExplorerView;

    public PaneDisplayOptions LoadLeftPaneDisplayOptions() => LoadSettings().LeftPaneDisplay;

    public PaneDisplayOptions LoadRightPaneDisplayOptions() => LoadSettings().RightPaneDisplay;

    public (bool IsEnabled, string? LeftPanePath, string? RightPanePath) LoadRememberPathSettings()
    {
        var settings = LoadSettings();
        return (settings.RememberPath, settings.LeftPanePath, settings.RightPanePath);
    }

    public bool LoadStartAsAdministrator() => LoadSettings().StartAsAdministrator;

    public void SaveStartAsAdministrator(bool value)
    {
        var settings = LoadSettings();
        settings.StartAsAdministrator = value;
        SaveSettings(settings);
    }

    public void SaveThemeMode(AppThemeMode themeMode)
    {
        var settings = LoadSettings();
        settings.ThemeMode = themeMode;
        SaveSettings(settings);
    }

    public void SaveAccentColorHex(string accentColorHex)
    {
        var settings = LoadSettings();
        settings.AccentColorHex = accentColorHex;
        SaveSettings(settings);
    }

    public void SaveExplorerViewOptions(ExplorerViewOptions explorerView)
    {
        var settings = LoadSettings();
        settings.ExplorerView = explorerView;
        SaveSettings(settings);
    }

    public void SaveLeftPaneDisplayOptions(PaneDisplayOptions displayOptions)
    {
        var settings = LoadSettings();
        settings.LeftPaneDisplay = displayOptions;
        SaveSettings(settings);
    }

    public void SaveRightPaneDisplayOptions(PaneDisplayOptions displayOptions)
    {
        var settings = LoadSettings();
        settings.RightPaneDisplay = displayOptions;
        SaveSettings(settings);
    }

    public void SaveRememberPathSettings(bool isEnabled, string? leftPanePath, string? rightPanePath)
    {
        var settings = LoadSettings();
        settings.RememberPath = isEnabled;
        settings.LeftPanePath = leftPanePath;
        settings.RightPanePath = rightPanePath;
        SaveSettings(settings);
    }

    /// <summary>Aplicativos escolhidos pelo usuário por extensão (".txt" → Notepad++).</summary>
    public IReadOnlyDictionary<string, (string DisplayName, string? ExecutablePath)> LoadDefaultApplications()
    {
        var result = new Dictionary<string, (string DisplayName, string? ExecutablePath)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (extension, entry) in LoadSettings().DefaultApplications ?? [])
        {
            if (entry is not null && !string.IsNullOrWhiteSpace(extension) && !string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                result[extension] = (entry.DisplayName, entry.ExecutablePath);
            }
        }

        return result;
    }

    public void SaveDefaultApplication(string extension, string displayName, string? executablePath)
    {
        var settings = LoadSettings();
        settings.DefaultApplications ??= new Dictionary<string, DefaultApplicationEntry>(StringComparer.OrdinalIgnoreCase);
        settings.DefaultApplications[extension] = new DefaultApplicationEntry { DisplayName = displayName, ExecutablePath = executablePath };
        SaveSettings(settings);
    }

    public void RemoveDefaultApplication(string extension)
    {
        var settings = LoadSettings();
        if (settings.DefaultApplications is not null && settings.DefaultApplications.Remove(extension))
        {
            SaveSettings(settings);
        }
    }

    public NetworkSettingsStore LoadNetworkSettings() => LoadSettings().Network;

    public NetworkProtocolSettings LoadNetworkProtocolSettings(NetworkProtocol protocol) =>
        CloneProtocolSettings(LoadNetworkSettings().GetServer(protocol));

    public void SaveNetworkProtocolSettings(NetworkProtocol protocol, NetworkProtocolSettings protocolSettings)
    {
        var settings = LoadSettings();
        settings.Network.SetServer(protocol, CloneProtocolSettings(protocolSettings));
        SaveSettings(settings);
    }

    public NetworkProtocolSettings LoadNetworkClientSettings(NetworkProtocol protocol) =>
        CloneProtocolSettings(LoadNetworkSettings().GetClient(protocol));

    public void SaveNetworkClientSettings(NetworkProtocol protocol, NetworkProtocolSettings protocolSettings)
    {
        var settings = LoadSettings();
        settings.Network.SetClient(protocol, CloneProtocolSettings(protocolSettings));
        SaveSettings(settings);
    }

    public IReadOnlyList<NetworkClientProfile> LoadNetworkClientProfiles(NetworkProtocol? protocol = null)
    {
        var profiles = LoadNetworkSettings().ClientProfiles ?? [];
        if (protocol is null)
        {
            return profiles.Select(CloneProfile).ToList();
        }

        return profiles
            .Where(p => p.Protocol == protocol.Value)
            .Select(CloneProfile)
            .ToList();
    }

    public void SaveNetworkClientProfile(NetworkClientProfile profile)
    {
        var settings = LoadSettings();
        settings.Network.ClientProfiles ??= [];
        var list = settings.Network.ClientProfiles;
        var index = list.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.Ordinal));
        var clone = CloneProfile(profile);
        if (index >= 0)
        {
            list[index] = clone;
        }
        else
        {
            list.Add(clone);
        }

        SaveSettings(settings);
    }

    public bool DeleteNetworkClientProfile(string profileId)
    {
        var settings = LoadSettings();
        settings.Network.ClientProfiles ??= [];
        var removed = settings.Network.ClientProfiles.RemoveAll(p =>
            string.Equals(p.Id, profileId, StringComparison.Ordinal));
        if (removed > 0)
        {
            SaveSettings(settings);
            return true;
        }

        return false;
    }

    public NetworkClientProfile? TryGetNetworkClientProfile(string profileId) =>
        LoadNetworkSettings().ClientProfiles
            ?.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal))
            is { } profile
            ? CloneProfile(profile)
            : null;

    private static NetworkClientProfile CloneProfile(NetworkClientProfile source) =>
        new()
        {
            Id = source.Id,
            Protocol = source.Protocol,
            DisplayName = source.DisplayName,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            RemotePath = string.IsNullOrWhiteSpace(source.RemotePath) ? "/" : source.RemotePath,
            LocalFilePath = source.LocalFilePath,
            RemoteFilePath = source.RemoteFilePath,
            IsUpload = source.IsUpload,
            ClientSecurity = source.ClientSecurity,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase
        };

    private static NetworkProtocolSettings CloneProtocolSettings(NetworkProtocolSettings source) =>
        new()
        {
            DisplayName = source.DisplayName,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            RemotePath = string.IsNullOrWhiteSpace(source.RemotePath) ? "/" : source.RemotePath,
            LocalFilePath = source.LocalFilePath,
            RemoteFilePath = source.RemoteFilePath,
            IsUpload = source.IsUpload,
            ReadOnly = source.ReadOnly,
            ServerSecurity = source.ServerSecurity,
            PassivePorts = source.PassivePorts,
            ClientSecurity = source.ClientSecurity,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase
        };

    private AppSettings LoadSettings()
    {
        lock (_ioLock)
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(_settingsFilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch (JsonException ex)
            {
                QuarantineUnreadableFile(ex);
                return new AppSettings();
            }
            catch (Exception ex)
            {
                // Falha de leitura (arquivo em uso, acesso negado...): usa os padrões sem mexer no arquivo.
                AppLogService.Warning($"Não foi possível ler as configurações: {ex.Message}");
                return new AppSettings();
            }
        }
    }

    private void SaveSettings(AppSettings settings)
    {
        lock (_ioLock)
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);

            // Grava num temporário e troca: um corte de energia ou falha a meio não deixa o arquivo truncado.
            var tempPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
    }

    // Conteúdo inválido: guarda o arquivo ao lado em vez de o apagar com o próximo salvamento.
    private void QuarantineUnreadableFile(JsonException error)
    {
        var backupPath = _settingsFilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Move(_settingsFilePath, backupPath, overwrite: true);
            AppLogService.Error($"As configurações estavam ilegíveis e foram guardadas em '{backupPath}'; foram repostos os padrões.", error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogService.Error($"As configurações estão ilegíveis e não foi possível guardá-las: {ex.Message}", error);
        }
    }

    private sealed class AppSettings
    {
        public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

        public string AccentColorHex { get; set; } = AccentColorPalette.Default.Hex;

        public ExplorerViewOptions ExplorerView { get; set; } = new();

        public PaneDisplayOptions LeftPaneDisplay { get; set; } = new();

        public PaneDisplayOptions RightPaneDisplay { get; set; } = new();

        public bool RememberPath { get; set; }

        public bool StartAsAdministrator { get; set; }

        public string? LeftPanePath { get; set; }

        public string? RightPanePath { get; set; }

        public NetworkSettingsStore Network { get; set; } = new();

        public Dictionary<string, DefaultApplicationEntry> DefaultApplications { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
