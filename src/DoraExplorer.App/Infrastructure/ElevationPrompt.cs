namespace DoraExplorer.App.Infrastructure;

internal enum ElevationPromptKind
{
    /// <summary>A mensagem não é de acesso negado: janela comum.</summary>
    None,

    /// <summary>Acesso negado e o app não é administrador: oferecer reiniciar como administrador.</summary>
    Offer,

    /// <summary>Acesso negado mesmo como administrador: só explicar.</summary>
    AlreadyElevated
}

/// <summary>Quando e como perguntar se o usuário quer reiniciar o aplicativo como administrador.</summary>
internal static class ElevationPrompt
{
    public const string RestartButton = "Reiniciar como administrador";

    public const string NotNowButton = "Agora não";

    public static ElevationPromptKind For(string message, bool isElevated)
    {
        if (!FileErrorText.IsAccessDenied(message))
        {
            return ElevationPromptKind.None;
        }

        return isElevated ? ElevationPromptKind.AlreadyElevated : ElevationPromptKind.Offer;
    }

    public static string OfferText(string message) =>
        FileErrorText.WithoutMarker(message) + Environment.NewLine + Environment.NewLine +
        "Esta operação precisa de permissão de administrador. Reiniciar o Dora Explorer como administrador? " +
        "O Windows vai pedir a sua confirmação, o aplicativo fecha e abre de novo, e então você repete a operação.";

    public static string AlreadyElevatedText(string message) =>
        FileErrorText.WithoutMarker(message) + Environment.NewLine + Environment.NewLine +
        "O Dora Explorer já está sendo executado como administrador. O item deve estar protegido pelo Windows " +
        "(por exemplo, pertencer ao TrustedInstaller) ou em uso por outro programa.";
}
