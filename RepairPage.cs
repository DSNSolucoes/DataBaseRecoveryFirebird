using System.Diagnostics;
using RecuperadorFirebird.Application;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird;

internal sealed class RepairPage : UserControl
{
    private sealed record Option(RepairLevel Level, string Title, string Description)
    {
        public override string ToString() => $"Nível {(int)Level} — {Title}";
    }

    private static readonly Option[] Options =
    [
        new(RepairLevel.Diagnostic, "Básico · Diagnóstico",
            "Examina páginas e registros com gfix -validate -full -no_update. Gera um relatório de diagnóstico."),
        new(RepairLevel.LogicalRebuild, "Médio · Reconstrução",
            "Cria um backup com gbak, restaura em um banco novo e valida. Mantém as verificações de integridade do backup."),
        new(RepairLevel.PageRepair, "Estrutural · Reparação de páginas",
            "Usa gfix -validate para permitir correções na cópia, depois tenta reconstruir o banco. Pode afetar dados danificados."),
        new(RepairLevel.Mend, "Avançado · Mend",
            "Valida páginas e registros, usa gfix -mend e tenta reconstruir o banco. O backup pode omitir registros danificados."),
        new(RepairLevel.IgnoreChecksums, "Último recurso · Mend e ignorar checksums",
            "Usa -ignore na validação, no mend e no backup. Pode omitir registros ou aceitar dados danificados.")
    ];

    private readonly TextBox source = new();
    private readonly TextBox destination = new();
    private readonly TextBox firebird = new();
    private readonly ComboBox level = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label description = new() { AutoSize = true };
    private readonly Label status = new() { AutoSize = true, Text = "Selecione o banco e a pasta de saída." };
    private readonly Button start = new() { Text = "&Executar nível", AutoSize = true };
    private readonly Button cancel = new() { Text = "&Cancelar", AutoSize = true, Enabled = false };
    private readonly Button open = new() { Text = "&Abrir pasta do resultado", AutoSize = true, Enabled = false };
    private readonly TextBox activity = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true,
        AccessibleName = "Atividade da reparação", BackColor = Color.White
    };
    private readonly List<Control> inputs = [];
    private CancellationTokenSource? cancellation;
    private string? resultFolder;
    public bool IsBusy => cancellation is not null;

    public RepairPage(RepairDatabase repair, string installation)
    {
        Dock = DockStyle.Fill;
        firebird.Text = installation;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 16), ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 163));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(layout);
        Wide(new Label { Text = "Reparar banco Firebird", AutoSize = true, Font = new Font("Segoe UI", 23, FontStyle.Bold) });
        Wide(new Label { Text = "Cada nível trabalha em uma cópia e salva os resultados em uma pasta nova.", AutoSize = true });
        Field("Banco de origem", source, "Selecionar arquivo", () =>
        {
            using var picker = new OpenFileDialog { CheckFileExists = true, Filter = "Bancos Firebird (*.fdb;*.foc;*.gdb)|*.fdb;*.foc;*.gdb|Todos os arquivos (*.*)|*.*" };
            if (picker.ShowDialog(this) == DialogResult.OK)
                source.Text = picker.FileName;
        });
        Field("Pasta de saída", destination, "Selecionar pasta", () => PickFolder(destination));
        Field("Ferramentas do Firebird", firebird, "Selecionar pasta", () => PickFolder(firebird));
        Field("Nível de reparação", level);
        level.Items.AddRange(Options);
        level.SelectedIndexChanged += (_, _) =>
        {
            if (level.SelectedItem is Option option)
                description.Text = option.Description;
        };
        level.SelectedIndex = 0;
        description.Margin = new Padding(0, 3, 0, 10);
        layout.SizeChanged += (_, _) => description.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
        Wide(description);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 4, 0, 8) };
        actions.Controls.AddRange([start, cancel, open]);
        Wide(actions);
        Wide(status);
        Wide(activity, new RowStyle(SizeType.Percent, 100));
        start.Click += async (_, _) => await Run();
        cancel.Click += (_, _) =>
        {
            cancel.Enabled = false;
            status.Text = "Cancelando...";
            cancellation?.Cancel();
        };
        open.Click += (_, _) =>
        {
            if (resultFolder is not null)
                Process.Start(new ProcessStartInfo(resultFolder) { UseShellExecute = true });
        };

        async Task Run()
        {
            if (level.SelectedItem is not Option selected)
                return;
            var input = source.Text.Trim();
            var output = destination.Text.Trim();
            var tools = firebird.Text.Trim();
            if (!File.Exists(input) || !Directory.Exists(output) || !Directory.Exists(tools))
            {
                status.Text = "Selecione o banco, a pasta de saída e a pasta do Firebird.";
                return;
            }
            var advanced = selected.Level >= RepairLevel.PageRepair;
            if (advanced && MessageBox.Show(this,
                $"{selected}\n\n{selected.Description}\n\nAceita o risco de perda de informações no resultado?",
                "Confirmar nível de reparação", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            using var tokenSource = new CancellationTokenSource();
            cancellation = tokenSource;
            resultFolder = null;
            activity.Clear();
            status.Text = "Executando o nível selecionado...";
            start.Enabled = open.Enabled = false;
            cancel.Enabled = true;
            foreach (var item in inputs)
                item.Enabled = false;
            const string folderMarker = "Pasta da execução: ";
            IProgress<string> progress = new Progress<string>(message =>
            {
                if (message.StartsWith(folderMarker, StringComparison.Ordinal))
                    resultFolder = message[folderMarker.Length..];
                activity.AppendText(message + Environment.NewLine);
            });
            try
            {
                var request = new RepairRequest(input, output, tools, selected.Level, advanced);
                var result = await Task.Run(() => repair.ExecuteAsync(request, progress.Report, tokenSource.Token));
                resultFolder = result.Folder;
                status.Text = result.Outcome switch
                {
                    RepairOutcome.DiagnosticClean => "Diagnóstico concluído",
                    RepairOutcome.RestoredAndValidated => "Restauração e validação concluídas",
                    RepairOutcome.Cancelled => "Execução cancelada",
                    _ => "Revise o resultado e os logs"
                };
                activity.AppendText(result.Message + Environment.NewLine);
            }
            catch (Exception error)
            {
                status.Text = "Confira os dados selecionados.";
                activity.AppendText(error.Message + Environment.NewLine);
            }
            finally
            {
                cancellation = null;
                start.Enabled = true;
                cancel.Enabled = false;
                open.Enabled = resultFolder is not null && Directory.Exists(resultFolder);
                foreach (var item in inputs)
                    item.Enabled = true;
            }
        }

        void Wide(Control control, RowStyle? style = null)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(style ?? new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 3);
        }

        void Field(string label, Control control, string? caption = null, Action? choose = null)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.AccessibleName = label;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            layout.Controls.Add(control, 1, row);
            inputs.Add(control);
            if (choose is null)
            {
                layout.SetColumnSpan(control, 2);
                return;
            }
            var button = new Button { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left };
            layout.Controls.Add(button, 2, row);
            inputs.Add(button);
            button.Click += (_, _) => choose();
        }
    }

    private void PickFolder(TextBox field)
    {
        using var picker = new FolderBrowserDialog();
        if (Directory.Exists(field.Text))
            picker.SelectedPath = field.Text;
        if (picker.ShowDialog(this) == DialogResult.OK)
            field.Text = picker.SelectedPath;
    }
}
