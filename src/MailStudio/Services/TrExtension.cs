using System.Windows.Markup;

namespace MailStudio;

/// <summary>XAML: <c>ToolTip="{local:Tr 'Eki kaldır'}"</c> — translated text for templates (DataTemplate / ControlTemplate).</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string text) => Text = text;
    [ConstructorArgument("text")] public string Text { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
