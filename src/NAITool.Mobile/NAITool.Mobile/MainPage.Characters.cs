using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAITool.Mobile.Core;

namespace NAITool.Mobile;

public sealed partial class MainPage
{
    private sealed record CharacterRow(
        StackPanel Host, TextBox Positive, TextBox Negative, CheckBox UseCoords, TextBox X, TextBox Y);

    private readonly List<CharacterRow> _additionalCharacters = [];

    private void OnAddCharacter(object sender, RoutedEventArgs e)
    {
        if (_additionalCharacters.Count >= 21) return; // Character 1 is always present.
        int number = _additionalCharacters.Count + 2;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = $"角色 {number}",
            VerticalAlignment = VerticalAlignment.Center,
        });
        var remove = new Button { Content = "移除", MinHeight = 38 };
        header.Children.Add(remove);

        var positive = new TextBox { PlaceholderText = $"角色 {number} · Prompt" };
        var negative = new TextBox { PlaceholderText = $"角色 {number} · Negative",
            IsEnabled = TagOf(EffortCombo) != "medium" || TagOf(ModelCombo) != "nai-diffusion-5-full" };
        var coordRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var useCoords = new CheckBox { Content = "自定义位置" };
        var x = new TextBox { Width = 62, Text = "0.5" };
        var y = new TextBox { Width = 62, Text = "0.5" };
        coordRow.Children.Add(useCoords);
        coordRow.Children.Add(x);
        coordRow.Children.Add(y);

        var root = new StackPanel { Spacing = 4, Padding = new Thickness(0, 8, 0, 8) };
        root.Children.Add(header);
        root.Children.Add(positive);
        root.Children.Add(negative);
        root.Children.Add(coordRow);

        var row = new CharacterRow(root, positive, negative, useCoords, x, y);
        _additionalCharacters.Add(row);
        ExtraCharactersHost.Children.Add(root);
        remove.Click += (_, _) =>
        {
            _additionalCharacters.Remove(row);
            ExtraCharactersHost.Children.Remove(root);
            UpdateCharacterLimitHint();
        };
        UpdateCharacterLimitHint();
    }

    private void UpdateCharacterLimitHint()
    {
        int max = MobileGenerationClient.MaxCharactersForModel(TagOf(ModelCombo, "nai-diffusion-5-full"));
        int active = 1 + _additionalCharacters.Count;
        // Retain V5 drafts when switching to a model with fewer character slots.
        AddCharacterButton.IsEnabled = active < 22;
        CharacterLimitText.Text = max == 0
            ? "当前模型不支持分角色提示词"
            : active > max
                ? $"该模型最多发送前 {max} 个角色，其余草稿会保留"
                : $"{active} / {max} 角色";
    }

    private static double Coordinate(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentException("角色位置请填写 0–1 之间的小数，例如 0.5。");
        return value;
    }

    private IReadOnlyList<MobileGenerationClient.Character> ReadCharacters(string model)
    {
        var characters = new List<MobileGenerationClient.Character>();
        void Add(string positive, string negative, bool useCoordinates, string x, string y)
        {
            if (string.IsNullOrWhiteSpace(positive)) return;
            characters.Add(new MobileGenerationClient.Character(positive.Trim(), negative.Trim(),
                useCoordinates ? Coordinate(x) : 0.5,
                useCoordinates ? Coordinate(y) : 0.5,
                useCoordinates));
        }

        Add(CharacterPromptBox.Text, CharacterNegativeBox.Text,
            FirstCharacterCoords.IsChecked == true, FirstCharacterX.Text, FirstCharacterY.Text);
        foreach (var row in _additionalCharacters)
            Add(row.Positive.Text, row.Negative.Text,
                row.UseCoords.IsChecked == true, row.X.Text, row.Y.Text);

        int max = MobileGenerationClient.MaxCharactersForModel(model);
        // Preserve hidden/more expensive V5 rows in UI; do not transmit unsupported slots.
        return characters.Take(max).ToArray();
    }
}
