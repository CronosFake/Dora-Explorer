using System.Runtime.InteropServices;

namespace DoraExplorer.App.Infrastructure;

/// <summary>Ajustes do mouse que o Windows guarda e o WPF não expõe.</summary>
internal static class SystemMouse
{
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>Tempo máximo, em milissegundos, entre dois cliques para valerem como duplo clique (500 por padrão).</summary>
    public static int DoubleClickMilliseconds
    {
        get
        {
            var value = (int)GetDoubleClickTime();
            return value is > 0 and <= 5000 ? value : 500;
        }
    }

    /// <summary>
    /// Quanto esperar, depois de um clique no nome de um item já selecionado, antes de começar a renomear: o tempo de duplo clique
    /// do Windows mais uma folga, para um duplo clique lento não ser confundido com um clique só.
    /// </summary>
    public static int SlowRenameDelayMilliseconds => DoubleClickMilliseconds + 100;
}
