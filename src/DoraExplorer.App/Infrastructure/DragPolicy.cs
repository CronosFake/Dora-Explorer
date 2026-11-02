using System.Runtime.InteropServices;
using System.Windows;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// O que um arrasto de itens pode fazer, conforme o destino. Entre os dois painéis do app vale copiar e mover; para qualquer outro
/// programa (Explorer, navegador, área de trabalho) vale só copiar, para arrastar nunca tirar um arquivo do lugar sem querer.
/// O Windows fixa os efeitos permitidos no começo do arrasto, então o arrasto é refeito só com Copiar quando o cursor sai do app.
/// </summary>
internal static class DragPolicy
{
    private const int VkLeftButton = 0x01;

    public static DragDropEffects AllowedEffects(bool cursorOverOwnWindow) =>
        cursorOverOwnWindow ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy;

    /// <summary>O cursor está sobre uma janela deste processo (o app, um menu ou uma caixa dele)? Sem saber, conta como dentro.</summary>
    public static bool IsCursorOverOwnWindow()
    {
        if (!GetCursorPos(out var point))
        {
            return true;
        }

        var window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>O botão esquerdo do mouse ainda está apertado (estado do teclado e do mouse no sistema, não o da fila do app)?</summary>
    public static bool IsLeftButtonDown() => (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
