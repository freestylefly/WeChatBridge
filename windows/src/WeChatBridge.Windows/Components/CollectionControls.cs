using System.Windows;
using System.Windows.Controls;

namespace WeChatBridge.Windows.Components;

public enum CollectionButtonKind { Primary, Secondary, Ghost, Icon, Segment, Danger }

/// <summary>Collection controls keep WPF's keyboard/automation behavior and replace every visual template.</summary>
public class CollectionButton : Button
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind),
        typeof(CollectionButtonKind), typeof(CollectionButton), new PropertyMetadata(CollectionButtonKind.Secondary));
    public CollectionButtonKind Kind { get => (CollectionButtonKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
}

public class CollectionTextBox : TextBox
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder),
        typeof(string), typeof(CollectionTextBox), new PropertyMetadata(""));
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
}

public class CollectionComboBox : ComboBox { }
public class CollectionCheckBox : CheckBox { }
public class CollectionExpander : Expander { }
public class CollectionScrollViewer : ScrollViewer { }
public class CollectionMenu : ContextMenu { }
public class CollectionMenuItem : MenuItem { }

public class CollectionTargetList : ListBox
{
    protected override DependencyObject GetContainerForItemOverride() => new CollectionTargetCard();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is CollectionTargetCard;
}
public class CollectionTargetCard : ListBoxItem { }

public class CollectionCard : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius),
        typeof(CornerRadius), typeof(CollectionCard), new PropertyMetadata(new CornerRadius(20)));
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
}
