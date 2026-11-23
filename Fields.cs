namespace XMLator;

internal interface IChoiceDescription
{
    string? Description { get; }
}

internal sealed record Choice<T>(T Value, string Label, string? Description = null) : IChoiceDescription
{
    public override string ToString() => Label;
}

internal sealed class Fields
{
    internal TableLayoutPanel Table { get; } = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        Dock = DockStyle.Top,
        Padding = new Padding(10, 5, 10, 6),
        GrowStyle = TableLayoutPanelGrowStyle.AddRows
    };

    internal Fields() => Table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

    public void Add(Control control)
    {
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        control.TabIndex = Table.RowCount;
        Table.Controls.Add(control, 0, Table.RowCount++);
    }

    public Label Note(string text)
    {
        var label = Caption(text);
        label.Margin = new Padding(3, 6, 3, 8);
        Add(label);
        return label;
    }

    public TextBox Entry(string label, string value, Action<string>? changed = null,
        bool secret = false, bool multiline = false)
    {
        var input = new TextBox
        {
            AccessibleName = label.Replace("&", ""),
            Text = value,
            UseSystemPasswordChar = secret,
            Multiline = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            AcceptsReturn = multiline,
            Height = multiline ? 118 : 27,
            Margin = new Padding(3, 1, 3, 9)
        };
        if (changed is not null)
            input.TextChanged += (_, _) => changed(input.Text);
        Labeled(label, input);
        return input;
    }

    public ComboBox Select<T>(string label, T value, IEnumerable<Choice<T>> options, Action<T>? changed = null)
    {
        var combo = new SelectionComboBox
        {
            AccessibleName = label.Replace("&", ""),
            DropDownStyle = ComboBoxStyle.DropDownList,
            DropDownHeight = 335,
            IntegralHeight = false,
            Margin = new Padding(3, 1, 3, 9)
        };
        UpdateChoices(combo, value, options);
        if (changed is not null)
            combo.SelectedIndexChanged += (_, _) =>
            {
                if (combo.SelectedItem is Choice<T> c)
                    changed(c.Value);
            };
        Labeled(label, combo);
        return combo;
    }

    public static void UpdateChoices<T>(ComboBox combo, T value, IEnumerable<Choice<T>> options)
    {
        var items = options.ToArray();
        int index = Array.FindIndex(items, choice => EqualityComparer<T>.Default.Equals(choice.Value, value));
        combo.BeginUpdate();
        try
        {
            combo.Items.Clear();
            combo.Items.AddRange(items.Cast<object>().ToArray());
            if (items.Length > 0)
                combo.SelectedIndex = Math.Max(index, 0);
        }
        finally { combo.EndUpdate(); }
    }

    public NumericUpDown Number(string label, int value, int minimum, int maximum, Action<int> changed)
    {
        var input = new NumericUpDown
        {
            AccessibleName = label.Replace("&", ""),
            Minimum = minimum, Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            Margin = new Padding(3, 1, 3, 9)
        };
        foreach (var editor in input.Controls.OfType<TextBox>())
            editor.AccessibleName = input.AccessibleName;
        input.ValueChanged += (_, _) => changed((int)input.Value);
        Labeled(label, input);
        return input;
    }

    public CheckBox Check(string label, bool value, Action<bool> changed)
    {
        var input = new OptionCheckBox
        {
            Text = label, AccessibleName = label.Replace("&", ""), AutoSize = true, Checked = value,
            Margin = new Padding(3, 4, 3, 6)
        };
        input.CheckedChanged += (_, _) => changed(input.Checked);
        Add(input);
        return input;
    }

    public Button Button(string text, Action? click = null)
    {
        var button = ActionButton(text, click);
        Add(button);
        return button;
    }

    public ListBox List(string name, IEnumerable<object> values, int height = 124)
    {
        var list = new ListBox
        {
            Height = height, IntegralHeight = false, AccessibleName = name, HorizontalScrollbar = true
        };
        list.Items.AddRange(values.ToArray());
        if (list.Items.Count > 0)
            list.SelectedIndex = 0;
        Labeled(name, list);
        return list;
    }

    public static void UpdateList(ListBox list, IEnumerable<string> values)
    {
        string[] items = values.ToArray();
        if (list.Items.Cast<string>().SequenceEqual(items))
            return;
        int selection = Math.Max(list.SelectedIndex, 0);
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            list.Items.AddRange(items);
            list.SelectedIndex = Math.Min(selection, items.Length - 1);
        }
        finally
        {
            list.EndUpdate();
        }
    }

    public void Section(Fields section) => Add(section.Table);

    public void Group(string name, Fields section)
    {
        var group = new GroupBox { Text = name, AccessibleName = name, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, TabStop = false };
        group.Controls.Add(section.Table);
        Add(group);
    }

    private void Labeled(string label, Control input)
    {
        Add(Caption(label));
        Add(input);
    }

    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 1), UseMnemonic = text.Contains('&')
    };

    public static Button ActionButton(string text, Action? click)
    {
        var button = new Button { Text = text, AccessibleName = text.Replace("&", ""), AutoSize = true, MinimumSize = new Size(112, 32) };
        if (click is not null)
            button.Click += (_, _) => click();
        return button;
    }
}
