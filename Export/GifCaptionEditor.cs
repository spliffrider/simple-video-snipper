namespace SimpleVideoSnipper;

internal sealed class GifCaptionEditor : TableLayoutPanel
{
    private readonly TextBox text = new()
    {
        Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = 50,
        Dock = DockStyle.Top, AccessibleName = "GIF caption",
    };
    private readonly ComboBox position = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, AccessibleName = "Caption position",
    };
    private readonly ComboBox font = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, DropDownWidth = 240, AccessibleName = "Caption font",
    };
    private readonly NumericUpDown size = new()
    {
        Minimum = 8, Maximum = 720, Value = 28, Width = 50, AccessibleName = "Caption font size in pixels",
    };
    private readonly CheckBox autoSize = new() { Text = "Auto size", AutoSize = true, Margin = new Padding(6, 6, 4, 0) };
    private readonly CheckBox bold = new() { Text = "Bold", AutoSize = true, Margin = new Padding(6, 6, 4, 0) };
    private readonly CheckBox outline = new() { Text = "Outline", AutoSize = true, Margin = new Padding(3, 6, 4, 0) };
    private readonly NumericUpDown outlineWidth = new()
    {
        Minimum = 1, Maximum = 16, Value = 2, Width = 45, AccessibleName = "Caption outline width in pixels",
    };
    private readonly CheckBox background = new() { Text = "Dark background", AutoSize = true, Margin = new Padding(10, 6, 4, 0) };
    private readonly Button textColor = new() { Text = "Text color…", AutoSize = true, UseVisualStyleBackColor = false };
    private readonly Button outlineColor = new() { Text = "Outline color…", AutoSize = true, UseVisualStyleBackColor = false };
    private PointF customPosition;

    public GifCaptionEditor(GifCaption? initial)
    {
        var caption = initial ?? new GifCaption("");
        customPosition = caption.CustomPosition;
        text.Text = caption.Text.ReplaceLineEndings();
        position.Items.AddRange(Enum.GetValues<GifCaptionPosition>().Cast<object>().ToArray());
        position.SelectedItem = caption.Position;
        font.Items.AddRange(System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source)
            .Append(GifCaption.DefaultFont).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).Cast<object>().ToArray());
        font.SelectedItem = caption.FontName;
        if (font.SelectedIndex < 0)
            font.SelectedItem = GifCaption.DefaultFont;
        size.Value = caption.FontSize ?? 28;
        autoSize.Checked = caption.FontSize is null;
        bold.Checked = caption.Bold;
        outline.Checked = caption.Outline;
        outlineWidth.Value = caption.OutlineWidth;
        background.Checked = caption.Background;
        SetColor(textColor, caption.TextColor);
        SetColor(outlineColor, caption.OutlineColor);

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 6);
        ColumnCount = 4;
        RowCount = 3;
        for (var row = 0; row < RowCount; row++)
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(Label("Caption"), 0, 0);
        Controls.Add(text, 1, 0);
        Controls.Add(Label("Position"), 2, 0);
        Controls.Add(position, 3, 0);
        var fontRow = Row(Label("Font"), font, Label("Size (px)"), size, autoSize, bold, textColor);
        var outlineRow = Row(outline, outlineColor, Label("Width (px)"), outlineWidth, background);
        Controls.Add(fontRow, 0, 1);
        Controls.Add(outlineRow, 0, 2);
        SetColumnSpan(fontRow, 4);
        SetColumnSpan(outlineRow, 4);

        text.TextChanged += (_, _) => NotifyChanged();
        position.SelectedIndexChanged += (_, _) => NotifyChanged();
        font.SelectedIndexChanged += (_, _) => NotifyChanged();
        size.ValueChanged += (_, _) => NotifyChanged();
        outlineWidth.ValueChanged += (_, _) => NotifyChanged();
        foreach (var box in new[] { autoSize, bold, outline, background })
            box.CheckedChanged += (_, _) => NotifyChanged();
        textColor.Click += (_, _) => ChooseColor(textColor);
        outlineColor.Click += (_, _) => ChooseColor(outlineColor);
        UpdateControls();
    }

    public event Action? Changed;

    public GifCaption? Caption => string.IsNullOrWhiteSpace(text.Text) ? null : new GifCaption(text.Text.Trim().ReplaceLineEndings())
    {
        Position = (GifCaptionPosition)position.SelectedItem!,
        CustomPosition = customPosition,
        FontName = (string)font.SelectedItem!,
        FontSize = autoSize.Checked ? null : (int)size.Value,
        Bold = bold.Checked,
        TextColor = textColor.BackColor,
        Outline = outline.Checked,
        OutlineColor = outlineColor.BackColor,
        OutlineWidth = (int)outlineWidth.Value,
        Background = background.Checked,
    };

    public void MoveTo(PointF point)
    {
        customPosition = point;
        if (Equals(position.SelectedItem, GifCaptionPosition.Custom))
            NotifyChanged();
        else
            position.SelectedItem = GifCaptionPosition.Custom;
    }

    private void NotifyChanged()
    {
        UpdateControls();
        Changed?.Invoke();
    }

    private void UpdateControls()
    {
        var hasCaption = !string.IsNullOrWhiteSpace(text.Text);
        position.Enabled = font.Enabled = autoSize.Enabled = bold.Enabled = textColor.Enabled = outline.Enabled =
            background.Enabled = hasCaption;
        size.Enabled = hasCaption && !autoSize.Checked;
        outlineColor.Enabled = outlineWidth.Enabled = hasCaption && outline.Checked;
    }

    private void ChooseColor(Button button)
    {
        using var picker = new ColorDialog { Color = button.BackColor, FullOpen = true, AnyColor = true };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        SetColor(button, picker.Color);
        NotifyChanged();
    }

    private static void SetColor(Button button, Color color)
    {
        button.BackColor = color;
        button.ForeColor = color.R * 299 + color.G * 587 + color.B * 114 > 140_000 ? Color.Black : Color.White;
    }

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 0) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0),
            WrapContents = false,
        };
        row.Controls.AddRange(controls);
        return row;
    }
}
