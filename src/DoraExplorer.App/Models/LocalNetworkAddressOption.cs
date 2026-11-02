namespace DoraExplorer.App.Models;

public sealed class LocalNetworkAddressOption
{
    public required string IpAddress { get; init; }

    public required string InterfaceName { get; init; }

    public string DisplayText => $"{IpAddress} ({InterfaceName})";

    public override string ToString() => DisplayText;
}
