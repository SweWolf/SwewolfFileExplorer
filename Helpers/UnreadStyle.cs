using System.ComponentModel;
using System.Windows;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The font an unread file gets in the list (Settings > Unread Files: bold and/or italic). The list binds to the one
/// <see cref="Instance"/>, so a change in Settings shows at once.
/// </summary>
public sealed class UnreadStyle : INotifyPropertyChanged
{
    public static UnreadStyle Instance { get; } = new();

    public FontWeight Weight => AppSettingsStore.Current.UnreadBold ? FontWeights.Bold : FontWeights.Normal;
    public FontStyle Style => AppSettingsStore.Current.UnreadItalic ? FontStyles.Italic : FontStyles.Normal;

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Weight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Style)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
