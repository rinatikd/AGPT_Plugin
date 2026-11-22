// Minimal AutoCAD-native AGPT palette. UI shell only: no calculation logic is duplicated here.
using System;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AGPT.UI.AgptPaletteCommands))]

namespace AGPT.UI
{
    public sealed class AgptPaletteCommands
    {
        private static PaletteSet _palette;

        [CommandMethod("AGPT_UI")]
        [CommandMethod("AGPT_PANEL")]
        public void ShowPalette()
        {
            if (_palette == null)
            {
                _palette = new PaletteSet("AGPT — управление");
                _palette.MinimumSize = new Size(300, 360);
                _palette.Size = new Size(340, 520);
                _palette.Add("АГПТ / АППТ", new AgptPanel());
            }

            _palette.Visible = true;
        }
    }

    internal sealed class AgptPanel : UserControl
    {
        private readonly Label _documentLabel;
        private readonly Label _statusLabel;

        public AgptPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Control;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(10),
                AutoScroll = true
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var heading = new Label
            {
                Text = "AGPT / APPT",
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 14, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 8)
            };
            root.Controls.Add(heading);

            _documentLabel = new Label { AutoSize = true, MaximumSize = new Size(280, 0), Margin = new Padding(0, 0, 0, 12) };
            root.Controls.Add(_documentLabel);

            root.Controls.Add(MakeButton("Создать / выбрать зону", "AGPT_ZONE"));
            root.Controls.Add(MakeButton("Список зон чертежа", "AGPT_ZONE_LIST"));

            var calcTitle = new Label
            {
                Text = "Расчёт и выпуск",
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                Margin = new Padding(0, 14, 0, 4)
            };
            root.Controls.Add(calcTitle);

            _statusLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(280, 0),
                ForeColor = Color.DarkGoldenrod,
                Text = "Расчётная кнопка будет активирована после интеграции GasCalc и отключения legacy/demo-пути (M1).",
                Margin = new Padding(0, 0, 0, 10)
            };
            root.Controls.Add(_statusLabel);

            var refresh = new Button { Text = "Обновить сведения", AutoSize = true, Dock = DockStyle.Top };
            refresh.Click += (s, e) => RefreshDocumentInfo();
            root.Controls.Add(refresh);

            RefreshDocumentInfo();
        }

        private static Button MakeButton(string caption, string command)
        {
            var button = new Button
            {
                Text = caption,
                AutoSize = true,
                Dock = DockStyle.Top,
                Height = 34,
                Margin = new Padding(0, 3, 0, 3)
            };
            button.Click += (s, e) =>
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    MessageBox.Show("Сначала откройте чертёж AutoCAD.", "AGPT");
                    return;
                }
                doc.SendStringToExecute(command + "\\n", true, false, false);
            };
            return button;
        }

        private void RefreshDocumentInfo()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            _documentLabel.Text = doc == null
                ? "Активный чертёж: отсутствует"
                : "Активный чертёж: " + doc.Name;
        }
    }
}
