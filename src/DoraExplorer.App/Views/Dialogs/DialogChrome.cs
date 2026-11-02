using System.Windows;

namespace DoraExplorer.App.Views.Dialogs;

internal static class DialogChrome
{
    public static void Apply(Window dialog, Window? owner, bool setOwner = true)
    {
        if (owner is not null)
        {
            if (setOwner)
            {
                dialog.Owner = owner;
            }

            dialog.Icon = owner.Icon;
        }

        App.ThemeManager.ApplyTitleBar(dialog);
    }
}
