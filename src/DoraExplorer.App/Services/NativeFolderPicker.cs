using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DoraExplorer.App.Services;

/// <summary>
/// Seletor nativo de pastas do Windows (IFileOpenDialog), sem depender de WinForms.
/// </summary>
internal static class NativeFolderPicker
{
    private static readonly Guid FileOpenDialogGuid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IShellItemGuid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    public static bool TryPickFolder(Window owner, string? initialPath, out string selectedPath)
    {
        selectedPath = string.Empty;

        var dialog = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOpenDialogGuid)!)!;
        try
        {
            dialog.SetOptions(
                Fos.PickFolders
                | Fos.ForceFileSystem
                | Fos.PathMustExist);

            dialog.SetTitle("Selecionar pasta raiz");

            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                var hr = SHCreateItemFromParsingName(initialPath, IntPtr.Zero, IShellItemGuid, out var folder);
                if (hr == 0 && folder is not null)
                {
                    dialog.SetFolder(folder);
                }
            }

            var hwnd = owner is null
                ? IntPtr.Zero
                : new WindowInteropHelper(owner).EnsureHandle();

            if (dialog.Show(hwnd) != 0)
            {
                return false;
            }

            dialog.GetResult(out var item);
            if (item is null)
            {
                return false;
            }

            item.GetDisplayName(Sigdn.FileSysPath, out var path);
            selectedPath = path ?? string.Empty;
            return !string.IsNullOrWhiteSpace(selectedPath);
        }
        finally
        {
            _ = Marshal.ReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint cFileTypes, [In] IntPtr rgFilterSpec);

        void SetFileTypeIndex(uint iFileType);

        void GetFileTypeIndex(out uint piFileType);

        void Advise(IntPtr pfde, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOptions(Fos fos);

        void GetOptions(out Fos pfos);

        void SetDefaultFolder(IShellItem psi);

        void SetFolder(IShellItem psi);

        void GetFolder(out IShellItem ppsi);

        void GetCurrentSelection(out IShellItem ppsi);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

        void GetResult(out IShellItem ppsi);

        void AddPlace(IShellItem psi, int fdap);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

        void Close([MarshalAs(UnmanagedType.Error)] int hr);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(IntPtr pFilter);

        void GetResults(out IntPtr ppenum);

        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(Sigdn sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [Flags]
    private enum Fos : uint
    {
        ForceFileSystem = 0x40,
        PathMustExist = 0x800,
        PickFolders = 0x20
    }

    private enum Sigdn : uint
    {
        FileSysPath = 0x80058000
    }
}
