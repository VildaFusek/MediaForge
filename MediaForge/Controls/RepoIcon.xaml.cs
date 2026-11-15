using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace MediaForge.Controls;

public sealed partial class RepoIcon : UserControl
{
    public RepoIcon()
    {
        InitializeComponent();
        IsEnabledChanged += (_, _) => IconPath.Opacity = IsEnabled ? 1 : 0.35;
    }

    public string Data
    {
        get => (string)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(string), typeof(RepoIcon), new PropertyMetadata("", OnDataChanged));

    private static void OnDataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var icon = (RepoIcon)sender;
        string data = (string)args.NewValue;
        icon.IconPath.Data = string.IsNullOrWhiteSpace(data) ? null
            : (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);
    }
}
