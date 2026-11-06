using System.Windows;
using System.Windows.Controls;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;

namespace DoraExplorer.App.Views.Dialogs;

/// <summary>
/// Diálogo de cliente remoto (nós FTP/SFTP/SCP/TFTP/SMB/NFS nos painéis).
/// </summary>
public partial class NetworkClientDialog : Window
{
    private readonly bool _isConnected;
    private readonly string? _editingProfileId;
    private bool _loading = true;

    public NetworkClientDialog(NetworkProtocol protocol, bool isConnected, NetworkClientProfile? editProfile = null)
    {
        InitializeComponent();
        Protocol = protocol;
        _isConnected = isConnected;
        _editingProfileId = editProfile?.Id;
        Title = protocol switch
        {
            NetworkProtocol.Ftp => "Cliente FTP",
            NetworkProtocol.Sftp => "Cliente SFTP",
            NetworkProtocol.Scp => "Cliente SCP",
            NetworkProtocol.Tftp => "Cliente TFTP",
            NetworkProtocol.Smb => "Cliente SMB",
            NetworkProtocol.Nfs => "Cliente NFS",
            _ => "Cliente de rede"
        };

        var isTransferOnly = NetworkKinds.IsTransferOnly(protocol);
        BrowsePanel.Visibility = isTransferOnly ? Visibility.Collapsed : Visibility.Visible;
        TransferPanel.Visibility = isTransferOnly ? Visibility.Visible : Visibility.Collapsed;
        ApplyProtocolFields();

        if (editProfile is not null)
        {
            LoadProfile(editProfile);
        }
        else
        {
            LoadPersistedSettings();
        }

        PrimaryButton.Content = _isConnected && !isTransferOnly ? "Desligar" : "Ligar";
        _loading = false;
    }

    public NetworkProtocol Protocol { get; }

    public NetworkDialogAction Action { get; private set; } = NetworkDialogAction.Cancelled;

    public NetworkConnectionInfo? Result { get; private set; }

    public string? EditingProfileId => _editingProfileId;

    public static bool TryShow(
        Window owner,
        NetworkProtocol protocol,
        bool isConnected,
        out NetworkDialogAction action,
        out NetworkConnectionInfo? connection,
        out string? editingProfileId,
        NetworkClientProfile? editProfile = null)
    {
        var dialog = new NetworkClientDialog(protocol, isConnected, editProfile);
        DialogChrome.Apply(dialog, owner);
        var accepted = dialog.ShowDialog() == true;
        action = accepted ? dialog.Action : NetworkDialogAction.Cancelled;
        connection = dialog.Result;
        editingProfileId = dialog.EditingProfileId;
        return accepted;
    }

    private void LoadProfile(NetworkClientProfile profile)
    {
        NameTextBox.Text = profile.DisplayName;
        HostTextBox.Text = profile.Host;
        PortTextBox.Text = (profile.Port > 0 ? profile.Port : DefaultPort()).ToString();
        UsernameTextBox.Text = profile.Username;
        PasswordBox.Password = profile.Password ?? string.Empty;
        RemotePathTextBox.Text = string.IsNullOrWhiteSpace(profile.RemotePath) ? "/" : profile.RemotePath;
        LocalFileTextBox.Text = profile.LocalFilePath;
        RemoteFileTextBox.Text = profile.RemoteFilePath;
        UploadRadio.IsChecked = profile.IsUpload;
        DownloadRadio.IsChecked = !profile.IsUpload;
        SelectSecurity(profile.ClientSecurity);
        KeyPathTextBox.Text = profile.PrivateKeyPath;
        PassphraseBox.Password = profile.PrivateKeyPassphrase ?? string.Empty;
    }

    private void LoadPersistedSettings()
    {
        var settings = App.SettingsService.LoadNetworkClientSettings(Protocol);
        NameTextBox.Text = settings.DisplayName;
        HostTextBox.Text = settings.Host;
        PortTextBox.Text = (settings.Port > 0 ? settings.Port : DefaultPort()).ToString();
        UsernameTextBox.Text = settings.Username;
        PasswordBox.Password = settings.Password ?? string.Empty;
        RemotePathTextBox.Text = string.IsNullOrWhiteSpace(settings.RemotePath) ? "/" : settings.RemotePath;
        LocalFileTextBox.Text = settings.LocalFilePath;
        RemoteFileTextBox.Text = settings.RemoteFilePath;
        UploadRadio.IsChecked = settings.IsUpload;
        DownloadRadio.IsChecked = !settings.IsUpload;
        SelectSecurity(settings.ClientSecurity);
        KeyPathTextBox.Text = settings.PrivateKeyPath;
        PassphraseBox.Password = settings.PrivateKeyPassphrase ?? string.Empty;
    }

    /// <summary>Mostra só o que o protocolo usa: criptografia no FTP, chave no SFTP/SCP, sem login no TFTP e sem senha no NFS.</summary>
    private void ApplyProtocolFields()
    {
        var isFtp = Protocol == NetworkProtocol.Ftp;
        var isSsh = Protocol is NetworkProtocol.Sftp or NetworkProtocol.Scp;
        var hasLogin = Protocol != NetworkProtocol.Tftp;
        var hasPassword = hasLogin && Protocol != NetworkProtocol.Nfs;

        SetVisible(hasLogin, UsernameLabel, UsernameTextBox);
        SetVisible(hasPassword, PasswordLabel, PasswordBox);
        SetVisible(isFtp, SecurityLabel, SecurityComboBox);
        SetVisible(isSsh, KeyLabel, KeyPanel, PassphraseLabel, PassphraseBox);

        if (Protocol == NetworkProtocol.Nfs)
        {
            UsernameLabel.Text = "Usuário:grupo";
            UsernameTextBox.ToolTip = "Números do usuário e do grupo no servidor (UID:GID), como 1000:1000. Vazio = 0:0. O NFS não usa senha.";
        }
        else if (Protocol == NetworkProtocol.Smb)
        {
            UsernameTextBox.ToolTip = "Usuário do servidor; use DOMINIO\\usuario para uma conta de domínio.";
        }
    }

    private static void SetVisible(bool visible, params UIElement[] elements)
    {
        foreach (var element in elements)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SelectSecurity(FtpClientSecurity security) =>
        SecurityComboBox.SelectedItem = SecurityComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals((string?)item.Tag, security.ToString(), StringComparison.Ordinal))
            ?? SecurityComboBox.Items[0];

    private FtpClientSecurity SelectedSecurity() =>
        SecurityComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<FtpClientSecurity>(tag, out var security)
            ? security
            : FtpClientSecurity.Auto;

    // O FTPS implícito usa a porta 990; ao trocar de modo, ajusta a porta se ainda for a padrão do modo anterior.
    private void SecurityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return; // carregando o perfil: não mexe na porta que o usuário salvou
        }

        var implicitMode = SelectedSecurity() == FtpClientSecurity.Implicit;
        if (implicitMode && PortTextBox.Text == "21")
        {
            PortTextBox.Text = "990";
        }
        else if (!implicitMode && PortTextBox.Text == "990")
        {
            PortTextBox.Text = "21";
        }
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Chave privada", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            KeyPathTextBox.Text = dialog.FileName;
        }
    }

    private int DefaultPort() => NetworkKinds.DefaultPort(Protocol);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSettings(out var settings, out var connection))
        {
            return;
        }

        App.SettingsService.SaveNetworkClientSettings(Protocol, settings);
        Result = connection;
        Action = NetworkDialogAction.Save;
        DialogResult = true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        var isTransferOnly = NetworkKinds.IsTransferOnly(Protocol);
        if (_isConnected && !isTransferOnly)
        {
            PersistCurrentFields();
            Action = NetworkDialogAction.Stop;
            DialogResult = true;
            return;
        }

        if (!TryReadSettings(out var settings, out var connection))
        {
            return;
        }

        App.SettingsService.SaveNetworkClientSettings(Protocol, settings);
        Result = connection;
        Action = NetworkDialogAction.Start;
        DialogResult = true;
    }

    private void PersistCurrentFields()
    {
        _ = int.TryParse(PortTextBox.Text, out var port);
        App.SettingsService.SaveNetworkClientSettings(Protocol, new NetworkProtocolSettings
        {
            DisplayName = NameTextBox.Text.Trim(),
            Host = HostTextBox.Text.Trim(),
            Port = port is >= 1 and <= 65535 ? port : DefaultPort(),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            RemotePath = string.IsNullOrWhiteSpace(RemotePathTextBox.Text) ? "/" : RemotePathTextBox.Text.Trim(),
            LocalFilePath = LocalFileTextBox.Text.Trim(),
            RemoteFilePath = RemoteFileTextBox.Text.Trim(),
            IsUpload = UploadRadio.IsChecked == true,
            ClientSecurity = SelectedSecurity(),
            PrivateKeyPath = KeyPathTextBox.Text.Trim(),
            PrivateKeyPassphrase = PassphraseBox.Password
        });
    }

    private bool TryReadSettings(out NetworkProtocolSettings settings, out NetworkConnectionInfo connection)
    {
        settings = new NetworkProtocolSettings();
        connection = null!;

        var displayName = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            MessageDialog.Show(this, "Indique o nome.", Title);
            return false;
        }

        var host = HostTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageDialog.Show(this, "Indique o servidor.", Title);
            return false;
        }

        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            MessageDialog.Show(this, "Porta inválida.", Title);
            return false;
        }

        if ((Protocol is NetworkProtocol.Sftp or NetworkProtocol.Scp) &&
            string.IsNullOrWhiteSpace(UsernameTextBox.Text))
        {
            MessageDialog.Show(this, "Indique o utilizador.", Title);
            return false;
        }

        if (Protocol == NetworkProtocol.Nfs && !RpcIdentity.TryParse(UsernameTextBox.Text, out _))
        {
            MessageDialog.Show(this, "Informe o usuário NFS como o número do usuário e do grupo no servidor, por exemplo 1000:1000, ou deixe vazio.", Title);
            return false;
        }

        var keyPath = KeyPathTextBox.Text.Trim();
        if ((Protocol is NetworkProtocol.Sftp or NetworkProtocol.Scp) &&
            keyPath.Length > 0 && !System.IO.File.Exists(keyPath))
        {
            MessageDialog.Show(this, "A chave privada indicada não existe.", Title);
            return false;
        }

        if (Protocol is NetworkProtocol.Scp or NetworkProtocol.Tftp)
        {
            if (string.IsNullOrWhiteSpace(LocalFileTextBox.Text))
            {
                MessageDialog.Show(this, "Indique o ficheiro local.", Title);
                return false;
            }

            if (string.IsNullOrWhiteSpace(RemoteFileTextBox.Text))
            {
                MessageDialog.Show(this, "Indique o ficheiro remoto.", Title);
                return false;
            }
        }

        settings = new NetworkProtocolSettings
        {
            DisplayName = displayName,
            Host = host,
            Port = port,
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            RemotePath = string.IsNullOrWhiteSpace(RemotePathTextBox.Text) ? "/" : RemotePathTextBox.Text.Trim(),
            LocalFilePath = LocalFileTextBox.Text.Trim(),
            RemoteFilePath = RemoteFileTextBox.Text.Trim(),
            IsUpload = UploadRadio.IsChecked == true,
            ClientSecurity = SelectedSecurity(),
            PrivateKeyPath = keyPath,
            PrivateKeyPassphrase = PassphraseBox.Password
        };

        connection = new NetworkConnectionInfo
        {
            Protocol = Protocol,
            DisplayName = settings.DisplayName,
            Host = settings.Host,
            Port = settings.Port,
            Username = settings.Username,
            Password = settings.Password,
            RemotePath = settings.RemotePath,
            LocalFilePath = settings.LocalFilePath,
            RemoteFilePath = settings.RemoteFilePath,
            IsUpload = settings.IsUpload,
            ClientSecurity = settings.ClientSecurity,
            PrivateKeyPath = settings.PrivateKeyPath,
            PrivateKeyPassphrase = settings.PrivateKeyPassphrase
        };

        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PersistCurrentFields();
        DialogResult = false;
    }

    private void BrowseLocal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        if (dialog.ShowDialog(this) == true)
        {
            LocalFileTextBox.Text = dialog.FileName;
        }
    }
}
