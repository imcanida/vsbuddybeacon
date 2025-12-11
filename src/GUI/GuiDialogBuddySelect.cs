using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;

namespace VSBuddyBeacon
{
    public class GuiDialogBuddySelect : GuiDialog
    {
        private string[] allNames = Array.Empty<string>();
        private string[] filteredNames = Array.Empty<string>();
        private string selectedName = null;
        private string searchText = "";
        private int selectedColorIndex = 0;
        private readonly Action<string, int> onConfirm;
        private readonly HashSet<int> usedColorIndices;

        public static readonly (double r, double g, double b)[] PinColors = new[]
        {
            (0.2, 0.6, 1.0),   // Blue
            (1.0, 0.4, 0.4),   // Red
            (0.4, 1.0, 0.4),   // Green
            (1.0, 0.8, 0.2),   // Yellow
            (1.0, 0.4, 1.0),   // Magenta
            (0.4, 1.0, 1.0),   // Cyan
            (1.0, 0.6, 0.2),   // Orange
            (0.8, 0.4, 1.0),   // Purple
        };

        public override string ToggleKeyCombinationCode => null;
        public override double DrawOrder => 0.9;

        public GuiDialogBuddySelect(ICoreClientAPI capi, string[] buddyNames, HashSet<int> usedColors, Action<string, int> onConfirmCallback) : base(capi)
        {
            allNames = buddyNames ?? Array.Empty<string>();
            filteredNames = allNames;
            usedColorIndices = usedColors ?? new HashSet<int>();
            onConfirm = onConfirmCallback;

            if (allNames.Length > 0)
            {
                selectedName = allNames[0];
            }

            // Find first unused color
            for (int i = 0; i < PinColors.Length; i++)
            {
                if (!usedColorIndices.Contains(i))
                {
                    selectedColorIndex = i;
                    break;
                }
            }

            ComposeDialog();
        }

        private void ComposeDialog()
        {
            int dialogHeight = allNames.Length == 0 ? 120 : 230;

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, 340, dialogHeight);

            var composer = capi.Gui
                .CreateCompo("vsbuddybeacon-buddyselect", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Pin Buddy", () => TryClose())
                .BeginChildElements(bgBounds);

            if (allNames.Length == 0)
            {
                composer.AddStaticText("No buddies available to pin",
                    CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }),
                    ElementBounds.Fixed(15, 50, 310, 25));

                composer.AddSmallButton("Close", () => { TryClose(); return true; },
                    ElementBounds.Fixed(110, 80, 120, 30), EnumButtonStyle.Normal);
            }
            else
            {
                double y = 40;

                // Search input
                composer.AddStaticText("Search:", CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(15, y + 3, 55, 20));

                composer.AddTextInput(
                    ElementBounds.Fixed(70, y, 250, 25),
                    OnSearchTextChanged,
                    CairoFont.WhiteSmallText(),
                    "searchInput"
                );
                y += 35;

                // Buddy dropdown
                composer.AddStaticText("Buddy:", CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(15, y + 5, 55, 20));

                if (filteredNames.Length > 0)
                {
                    int selectedIndex = Array.IndexOf(filteredNames, selectedName);
                    if (selectedIndex < 0) selectedIndex = 0;

                    composer.AddDropDown(
                        filteredNames,
                        filteredNames,
                        selectedIndex,
                        OnBuddyDropdownChanged,
                        ElementBounds.Fixed(70, y, 250, 28),
                        CairoFont.WhiteSmallText(),
                        "buddyDropdown"
                    );
                }
                else
                {
                    composer.AddStaticText("No matches found",
                        CairoFont.WhiteSmallText().WithColor(new double[] { 0.6, 0.6, 0.6, 1 }),
                        ElementBounds.Fixed(70, y + 5, 250, 20));
                }
                y += 40;

                // Color selection
                composer.AddStaticText("Color:", CairoFont.WhiteSmallText(),
                    ElementBounds.Fixed(15, y + 8, 55, 20));

                double colorX = 70;
                double colorSize = 28;
                double colorSpacing = 32;

                for (int i = 0; i < PinColors.Length; i++)
                {
                    int colorIdx = i;
                    var color = PinColors[i];
                    bool isUsed = usedColorIndices.Contains(i);

                    composer.AddDynamicCustomDraw(
                        ElementBounds.Fixed(colorX + (i * colorSpacing), y, colorSize, colorSize),
                        (ctx, surface, bounds) => DrawColorSwatch(ctx, bounds, colorIdx, color, isUsed),
                        $"color_{i}"
                    );

                    if (!isUsed)
                    {
                        composer.AddSmallButton("", () => OnColorClicked(colorIdx),
                            ElementBounds.Fixed(colorX + (i * colorSpacing), y, colorSize, colorSize),
                            EnumButtonStyle.None, $"colorbtn_{i}");
                    }
                }
                y += 45;

                // Buttons
                bool canPin = filteredNames.Length > 0;
                if (canPin)
                {
                    composer.AddSmallButton("Pin", OnConfirmClicked,
                        ElementBounds.Fixed(70, y, 100, 28), EnumButtonStyle.Normal);
                }

                composer.AddSmallButton("Cancel", () => { TryClose(); return true; },
                    ElementBounds.Fixed(canPin ? 190 : 120, y, 100, 28), EnumButtonStyle.Normal);
            }

            SingleComposer = composer.EndChildElements().Compose();

            // Set initial search text if recomposing
            if (!string.IsNullOrEmpty(searchText))
            {
                SingleComposer?.GetTextInput("searchInput")?.SetValue(searchText);
            }
        }

        private void OnSearchTextChanged(string text)
        {
            searchText = text ?? "";

            // Filter names
            if (string.IsNullOrWhiteSpace(searchText))
            {
                filteredNames = allNames;
            }
            else
            {
                filteredNames = allNames
                    .Where(n => n.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();
            }

            // Update selection
            if (filteredNames.Length > 0)
            {
                if (!filteredNames.Contains(selectedName))
                {
                    selectedName = filteredNames[0];
                }
            }
            else
            {
                selectedName = null;
            }

            // Recompose to update dropdown
            ComposeDialog();
        }

        private void DrawColorSwatch(Context ctx, ElementBounds bounds, int colorIdx, (double r, double g, double b) color, bool isUsed)
        {
            double w = bounds.OuterWidth;
            double h = bounds.OuterHeight;

            // Background
            ctx.SetSourceRGBA(color.r, color.g, color.b, isUsed ? 0.3 : 1.0);
            ctx.Rectangle(2, 2, w - 4, h - 4);
            ctx.Fill();

            // Border - highlight if selected
            if (colorIdx == selectedColorIndex && !isUsed)
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.LineWidth = 3;
            }
            else
            {
                ctx.SetSourceRGBA(0.4, 0.4, 0.4, 1);
                ctx.LineWidth = 1;
            }
            ctx.Rectangle(2, 2, w - 4, h - 4);
            ctx.Stroke();

            // X for used colors
            if (isUsed)
            {
                ctx.SetSourceRGBA(0.5, 0.5, 0.5, 0.8);
                ctx.LineWidth = 2;
                ctx.MoveTo(6, 6);
                ctx.LineTo(w - 6, h - 6);
                ctx.MoveTo(w - 6, 6);
                ctx.LineTo(6, h - 6);
                ctx.Stroke();
            }
        }

        private void OnBuddyDropdownChanged(string code, bool selected)
        {
            selectedName = code;
        }

        private bool OnColorClicked(int colorIndex)
        {
            if (usedColorIndices.Contains(colorIndex)) return true;

            selectedColorIndex = colorIndex;

            // Redraw color swatches to show selection
            for (int i = 0; i < PinColors.Length; i++)
            {
                SingleComposer?.GetCustomDraw($"color_{i}")?.Redraw();
            }

            return true;
        }

        private bool OnConfirmClicked()
        {
            if (string.IsNullOrEmpty(selectedName))
            {
                return true;
            }

            onConfirm?.Invoke(selectedName, selectedColorIndex);
            TryClose();
            return true;
        }
    }
}
