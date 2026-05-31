using Avalonia;
using Avalonia.Controls;

namespace Sable.App;

/// <summary>
/// Reusable settings/effect row: a label on the left, the supplied control on the right
/// (`&lt;c:SettingRow Label="…"&gt;&lt;ToggleSwitch/&gt;&lt;/c:SettingRow&gt;`). Templated in App.axaml.
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<SettingRow, string>(nameof(Label), "");

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
}
