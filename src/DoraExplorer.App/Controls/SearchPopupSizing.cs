namespace DoraExplorer.App.Controls;

/// <summary>Conta do tamanho do menu Localizar ao ser aumentado com a alça, separada da tela para ser testada.</summary>
internal static class SearchPopupSizing
{
    /// <summary>O valor dentro dos limites; <c>NaN</c> (tamanho ainda não medido) volta ao mínimo.</summary>
    public static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Min(Math.Max(value, min), max);
}
