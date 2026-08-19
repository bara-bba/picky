using System.Windows;

namespace Picky;

/// <summary>What the user chose in the update prompt.</summary>
internal enum UpdateChoice
{
    /// <summary>Dismiss for now; prompt again on the next launch / check.</summary>
    Later,

    /// <summary>Download and restart into the new version.</summary>
    UpdateNow,

    /// <summary>Don't prompt for this version again (automatic checks only).</summary>
    Skip,
}

public partial class UpdatePromptWindow : Window
{
    private UpdateChoice _choice = UpdateChoice.Later;

    private UpdatePromptWindow(string currentVersion, string availableVersion)
    {
        InitializeComponent();
        MessageText.Text =
            $"Picky v{availableVersion} is available. You're on v{currentVersion}.";
    }

    /// <summary>Shows the prompt modally and returns the user's choice.</summary>
    internal static UpdateChoice Ask(string currentVersion, string availableVersion)
    {
        var window = new UpdatePromptWindow(currentVersion, availableVersion);
        window.ShowDialog();
        return window._choice;
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        _choice = UpdateChoice.UpdateNow;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        _choice = UpdateChoice.Later;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _choice = UpdateChoice.Skip;
        Close();
    }
}
