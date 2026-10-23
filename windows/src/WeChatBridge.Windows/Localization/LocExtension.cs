using System.Windows.Markup;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => L10n.Text(Key);
}
