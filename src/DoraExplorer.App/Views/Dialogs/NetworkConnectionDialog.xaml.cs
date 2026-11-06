using System.IO;
using System.Windows;
using System.Windows.Controls;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DoraExplorer.App.Views.Dialogs;

/// <summary>
/// Diálogo de servidor local (menu central REDE SERVER).
/// </summary>
public partial class NetworkConnectionDialog : Window
{
    private static readonly Dictionary<NetworkProtocol, NetworkConnectionDialog> OpenWindows = new();

    private readonly NetworkServerLogStore _logs;
    private bool _isServiceRunning;
    private Action<NetworkDialogAction, NetworkConnectionInfo?>? _onCompleted;
    private bool _closing;

    public NetworkConnectionDialog(NetworkProtocol protocol, bool isServiceRunning)
    {
        InitializeComponent();
        Protocol = protocol;
        _isServiceRunning = isServiceRunning;
        _logs = App.Services.GetRequiredService<NetworkServerLogStore>();

        Title = protocol switch
        {
            NetworkProtocol.Ftp => "Servidor FTP",
            NetworkProtocol.Sftp => "Servidor SFTP",
            NetworkProtocol.Scp => "Servidor SCP",
            NetworkProtocol.Tftp => "Servidor TFTP",
            NetworkProtocol.Smb => "Servidor SMB",
            NetworkProtocol.Nfs => "Servidor NFS",
            _ => "Servidor de rede"
        };

        ApplyProtocolFields();
        LoadPersistedSettings();
        UpdatePrimaryButton();
        RefreshLogs();
        _logs.Changed += OnServerLogChanged;
        Closed += (_, _) => _logs.Changed -= OnServerLogChanged;
    }

    public NetworkProtocol Protocol { get; }

    public NetworkDialogAction Action { get; private set; } = NetworkDialogAction.Cancelled;

    public NetworkConnectionInfo? Result { get; private set; }

    public static void ShowOrActivate(
        Window owner,
        NetworkProtocol protocol,
        bool isServiceRunning,
        Action<NetworkDialogAction, NetworkConnectionInfo?> onCompleted)
    {
        if (OpenWindows.TryGetValue(protocol, out var existing))
        {
            existing._onCompleted = onCompleted;
            existing.SetRunningState(isServiceRunning);
            existing.RefreshLogs();
            BringToFront(existing);
            return;
        }

        var dialog = new NetworkConnectionDialog(protocol, isServiceRunning)
        {
            _onCompleted = onCompleted
        };

        DialogChrome.Apply(dialog, owner, setOwner: false);
        OpenWindows[protocol] = dialog;
        dialog.Closed += (_, _) =>
        {
            if (OpenWindows.TryGetValue(protocol, out var current) && ReferenceEquals(current, dialog))
            {
                OpenWindows.Remove(protocol);
            }
        };

        dialog.Show();
    }

    public void SetRunningState(bool isRunning)
    {
        _isServiceRunning = isRunning;
        UpdatePrimaryButton();
        RefreshLogs();
    }

    public static void NotifyRunningState(NetworkProtocol protocol, bool isRunning)
    {
        if (OpenWindows.TryGetValue(protocol, out var dialog))
        {
            dialog.SetRunningState(isRunning);
        }
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        _ = window.Focus();
    }

    private void LoadPersistedSettings()
    {
        var settings = App.SettingsService.LoadNetworkProtocolSettings(Protocol);
        var defaultPort = NetworkKinds.DefaultServerPort(Protocol);

        PopulateHostAddresses(settings.Host);
        PortTextBox.Text = (settings.Port > 0 ? settings.Port : defaultPort).ToString();
        UsernameTextBox.Text = settings.Username;
        PasswordBox.Password = settings.Password ?? string.Empty;

        // Sem pasta predefinida: expor a pasta do usuário por omissão seria inseguro.
        var root = settings.RemotePath;
        RootPathTextBox.Text = string.IsNullOrWhiteSpace(root) || root == "/" ? string.Empty : root;

        SecurityComboBox.SelectedItem = SecurityComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals((string?)item.Tag, settings.ServerSecurity.ToString(), StringComparison.Ordinal))
            ?? SecurityComboBox.Items[0];
        PassivePortsTextBox.Text = settings.PassivePorts;
        ReadOnlyCheckBox.IsChecked = settings.ReadOnly;
    }

    /// <summary>Mostra só o que o servidor usa: TFTP e NFS não têm login; FTPS e portas passivas são do FTP.</summary>
    private void ApplyProtocolFields()
    {
        var isFtp = Protocol == NetworkProtocol.Ftp;
        var hasLogin = HasLogin(Protocol);

        SetVisible(hasLogin, UsernameLabel, UsernameTextBox, PasswordLabel, PasswordBox);
        SetVisible(isFtp, SecurityLabel, SecurityComboBox, PassiveLabel, PassivePortsTextBox);

        if (Protocol == NetworkProtocol.Tftp)
        {
            ReadOnlyCheckBox.ToolTip = "O TFTP não tem senha: com esta opção ligada ninguém na rede consegue gravar arquivos.";
        }
        else if (Protocol == NetworkProtocol.Nfs)
        {
            ReadOnlyCheckBox.ToolTip = "O NFS não tem senha: com esta opção ligada ninguém na rede consegue alterar os arquivos. O servidor só atende endereços de rede local.";
        }
        else if (Protocol == NetworkProtocol.Smb)
        {
            PortTextBox.ToolTip = "O SMB padrão é a porta 445, que o próprio Windows costuma usar; a 4445 sobe sem mexer no sistema.";
        }
    }

    /// <summary>TFTP e NFS não têm usuário e senha no protocolo.</summary>
    private static bool HasLogin(NetworkProtocol protocol) => protocol is not (NetworkProtocol.Tftp or NetworkProtocol.Nfs);

    private static void SetVisible(bool visible, params UIElement[] elements)
    {
        foreach (var element in elements)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private FtpServerSecurity SelectedSecurity() =>
        SecurityComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<FtpServerSecurity>(tag, out var security)
            ? security
            : FtpServerSecurity.Optional;

    private void PopulateHostAddresses(string preferredHost)
    {
        var addresses = LocalNetworkAddressService.GetSortedIpv4Addresses();
        HostComboBox.ItemsSource = addresses;

        if (addresses.Count == 0)
        {
            HostComboBox.SelectedIndex = -1;
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferredHost))
        {
            var match = addresses.FirstOrDefault(a =>
                string.Equals(a.IpAddress, preferredHost.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                HostComboBox.SelectedItem = match;
                return;
            }
        }

        HostComboBox.SelectedIndex = 0;
    }

    private string? GetSelectedHost() =>
        HostComboBox.SelectedItem is LocalNetworkAddressOption option
            ? option.IpAddress
            : null;

    private void UpdatePrimaryButton()
    {
        PrimaryButton.Content = _isServiceRunning ? "Parar" : "Iniciar";
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_isServiceRunning)
        {
            PersistCurrentFields(requireValidPort: true, out var port);
            if (port is > 0 && NetworkPortGuard.IsPortUsedByOtherProcess(port, Protocol))
            {
                AppLogService.Info(
                    $"Parar {Protocol}: porta {port} em uso por outro processo — apenas a sessão Dora será limpa.");
            }

            Action = NetworkDialogAction.Stop;
            Result = null;
            _onCompleted?.Invoke(NetworkDialogAction.Stop, null);
            // Mantém a janela aberta; o log persiste após Parar.
            return;
        }

        if (!TryReadSettings(out var settings, out var connection))
        {
            return;
        }

        App.SettingsService.SaveNetworkProtocolSettings(Protocol, settings);
        Action = NetworkDialogAction.Start;
        Result = connection;
        _onCompleted?.Invoke(NetworkDialogAction.Start, connection);
        // Mantém a janela aberta para acompanhar a aba Logs.
    }

    private void PersistCurrentFields(bool requireValidPort, out int port)
    {
        port = 0;
        _ = int.TryParse(PortTextBox.Text, out port);
        if (requireValidPort && port is < 1 or > 65535)
        {
            var fallback = App.SettingsService.LoadNetworkProtocolSettings(Protocol).Port;
            port = fallback > 0 ? fallback : NetworkKinds.DefaultServerPort(Protocol);
        }

        App.SettingsService.SaveNetworkProtocolSettings(Protocol, new NetworkProtocolSettings
        {
            Host = GetSelectedHost() ?? string.Empty,
            Port = port is >= 1 and <= 65535 ? port : NetworkKinds.DefaultServerPort(Protocol),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            RemotePath = RootPathTextBox.Text.Trim(),
            ReadOnly = ReadOnlyCheckBox.IsChecked == true,
            ServerSecurity = SelectedSecurity(),
            PassivePorts = PassivePortsTextBox.Text.Trim()
        });
    }

    private bool TryReadSettings(out NetworkProtocolSettings settings, out NetworkConnectionInfo connection)
    {
        settings = new NetworkProtocolSettings();
        connection = null!;

        var host = GetSelectedHost();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageDialog.Show(this, "Selecione o endereço local.", Title);
            return false;
        }

        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageDialog.Show(this, "Porta inválida.", Title);
            return false;
        }

        var rootPath = RootPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            MessageDialog.Show(this, "Indique uma pasta raiz válida.", Title);
            return false;
        }

        if (HasLogin(Protocol) &&
            (string.IsNullOrWhiteSpace(UsernameTextBox.Text) || string.IsNullOrEmpty(PasswordBox.Password)))
        {
            MessageDialog.Show(this, $"O servidor {NetworkKinds.DisplayName(Protocol)} exige usuário e senha.", Title);
            return false;
        }

        var passivePorts = PassivePortsTextBox.Text.Trim();
        if (Protocol == NetworkProtocol.Ftp && !PassivePortRange.TryParse(passivePorts, out _, out var passiveError))
        {
            MessageDialog.Show(this, passiveError!, Title);
            return false;
        }

        settings = new NetworkProtocolSettings
        {
            Host = host,
            Port = port,
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            RemotePath = rootPath,
            ReadOnly = ReadOnlyCheckBox.IsChecked == true,
            ServerSecurity = SelectedSecurity(),
            PassivePorts = passivePorts
        };

        connection = new NetworkConnectionInfo
        {
            Protocol = Protocol,
            Host = settings.Host,
            Port = settings.Port,
            Username = settings.Username,
            Password = settings.Password,
            RemotePath = settings.RemotePath,
            ReadOnly = settings.ReadOnly,
            ServerSecurity = settings.ServerSecurity,
            PassivePorts = settings.PassivePorts
        };

        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PersistCurrentFields(requireValidPort: false, out _);
        Action = NetworkDialogAction.Cancelled;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing)
        {
            PersistCurrentFields(requireValidPort: false, out _);
            Action = NetworkDialogAction.Cancelled;
            _closing = true;
        }

        base.OnClosing(e);
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(RootPathTextBox.Text)
            ? RootPathTextBox.Text
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (NativeFolderPicker.TryPickFolder(this, initial, out var selectedPath))
        {
            RootPathTextBox.Text = selectedPath;
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshLogs();

    private void OnServerLogChanged(object? sender, NetworkProtocol protocol)
    {
        if (protocol != Protocol)
        {
            return;
        }

        Dispatcher.BeginInvoke(RefreshLogs);
    }

    private void RefreshLogs()
    {
        var text = _logs.GetText(Protocol);
        if (string.Equals(LogsTextBox.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        LogsTextBox.Text = text;
        LogsTextBox.CaretIndex = LogsTextBox.Text.Length;
        LogsTextBox.ScrollToEnd();
    }
}
