using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoraExplorer.App.Services;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Guarda strings sensíveis (senhas) cifradas com DPAPI no escopo do usuário atual.
/// Valores antigos, em texto puro, continuam a ser lidos e passam a ser cifrados no próximo salvamento.
/// </summary>
public sealed class ProtectedStringJsonConverter : JsonConverter<string>
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DoraExplorer.Settings.v1");

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var stored = reader.GetString();
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Arquivo copiado de outro usuário/máquina: a senha terá de ser digitada novamente.
            AppLogService.Warning("Não foi possível decifrar uma senha guardada nas configurações; será pedida novamente.");
            return string.Empty;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        writer.WriteStringValue(Prefix + Convert.ToBase64String(cipher));
    }
}
