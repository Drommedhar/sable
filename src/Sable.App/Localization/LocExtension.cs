using System;
using Avalonia;
using Avalonia.Markup.Xaml;

namespace Sable.App.Localization;

/// <summary>
/// Markup extension for localized strings (ported from Novalist; see docs/i18n-decision.md).
/// Usage: <c>{loc:Loc menu.file.open}</c>. Sets the target property now and re-sets it whenever
/// the UI language changes. Holds the target via a <see cref="WeakReference{T}"/> so the
/// <see cref="Loc.LanguageChanged"/> subscription never roots the visual tree (it self-unsubscribes
/// once the target is collected).
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; }
    public string? StringFormat { get; set; }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target
            && target.TargetObject is AvaloniaObject ao && target.TargetProperty is AvaloniaProperty ap)
        {
            UpdateValue(ao, ap);

            var weakTarget = new WeakReference<AvaloniaObject>(ao);
            var weakProp = ap;
            Action? handler = null;
            handler = () =>
            {
                if (weakTarget.TryGetTarget(out var liveTarget)) UpdateValue(liveTarget, weakProp);
                else if (handler != null) Loc.Instance.LanguageChanged -= handler;
            };
            Loc.Instance.LanguageChanged += handler;
        }
        return FormatValue(Loc.T(Key));
    }

    private void UpdateValue(AvaloniaObject ao, AvaloniaProperty ap) => ao.SetValue(ap, FormatValue(Loc.T(Key)));

    private object FormatValue(string value)
    {
        if (!string.IsNullOrEmpty(StringFormat))
        {
            try { return string.Format(StringFormat, value); }
            catch (FormatException) { }
        }
        return value;
    }
}
