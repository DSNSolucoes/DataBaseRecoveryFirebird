using RecuperadorFirebird.Application;

namespace RecuperadorFirebird;

internal sealed class SchemaCompletionPage : UserControl
{
    private readonly TextBox metadata = new();
    private readonly TextBox database = new();
    private readonly TextBox firebird = new();
    private readonly Button analyze = new() { Text = "&Verificar esquema", AutoSize = true };
    private readonly Button apply = new() { Text = "&Criar objetos faltantes", AutoSize = true, Enabled = false };
    private readonly Label status = new() { Text = "Selecione os dois arquivos.", AutoSize = true };
    private readonly TextBox details = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        AccessibleName = "Resultado da verificação do esquema", BackColor = Color.White
    };
    private readonly List<Control> inputs = [];
    private IReadOnlyList<SchemaChange> pending = [];
    public bool IsBusy { get; private set; }

    public SchemaCompletionPage(CompleteDatabaseSchema completion, string installation)
    {
        Dock = DockStyle.Fill;
        firebird.Text = installation;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 16), ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 163));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(layout);
        Wide(new Label { Text = "Completar esquema", AutoSize = true, Font = new Font("Segoe UI", 23, FontStyle.Bold) });
        Wide(new Label { Text = "Compare os metadados com o banco recuperado e revise os objetos faltantes.", AutoSize = true });
        Field("Arquivo de metadados", metadata, "Selecionar arquivo", () => PickFile(metadata, "SQL (*.sql)|*.sql|Todos os arquivos (*.*)|*.*"));
        Field("Banco recuperado", database, "Selecionar arquivo", () => PickFile(database, "Bancos Firebird (*.fdb;*.foc;*.gdb)|*.fdb;*.foc;*.gdb|Todos os arquivos (*.*)|*.*"));
        Field("Ferramentas do Firebird", firebird, "Selecionar pasta", () =>
        {
            using var picker = new FolderBrowserDialog();
            if (picker.ShowDialog(this) == DialogResult.OK)
                firebird.Text = picker.SelectedPath;
        });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
        actions.Controls.AddRange([analyze, apply]);
        Wide(actions);
        Wide(status);
        Wide(details, new RowStyle(SizeType.Percent, 100));
        analyze.Click += async (_, _) => await Execute(false);
        apply.Click += async (_, _) => await Execute(true);

        async Task Execute(bool write)
        {
            var request = new SchemaCompletionRequest(metadata.Text.Trim(), database.Text.Trim(), firebird.Text.Trim());
            if (write && MessageBox.Show(this,
                $"Criar os objetos listados neste banco?\n\n{request.Database}\n\nUse uma cópia do banco recuperado.",
                "Confirmar alteração do esquema", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            IsBusy = true;
            analyze.Enabled = apply.Enabled = false;
            foreach (var input in inputs)
                input.Enabled = false;
            status.Text = write ? "Aplicando e verificando o esquema..." : "Verificando o esquema...";
            try
            {
                pending = write ? await completion.ApplyAsync(request, pending) : await completion.AnalyzeAsync(request);
                details.Lines = pending.Select(item => $"{item.Kind} — {item.Name}" +
                    (item.BlockedReason is null ? "" : $" [bloqueado: {item.BlockedReason}]")).Distinct().ToArray();
                var ready = pending.Where(item => item.BlockedReason is null).Select(item => (item.Kind, item.Name)).Distinct().Count();
                status.Text = pending.Count == 0
                    ? (write ? "Alterações aplicadas e verificadas." : "Nenhum objeto faltante encontrado.")
                    : $"{ready} prontos para criar · {details.Lines.Length - ready} bloqueados.";
            }
            catch (Exception error)
            {
                pending = [];
                details.Text = error.Message;
                status.Text = "Não foi possível concluir. Confira os detalhes.";
            }
            finally
            {
                IsBusy = false;
                analyze.Enabled = true;
                apply.Enabled = pending.Any(item => item.BlockedReason is null);
                foreach (var input in inputs)
                    input.Enabled = true;
            }
        }

        void Wide(Control control, RowStyle? style = null)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(style ?? new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 3);
        }

        void Field(string label, TextBox input, string caption, Action choose)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            input.AccessibleName = label;
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            layout.Controls.Add(input, 1, row);
            var button = new Button { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left };
            layout.Controls.Add(button, 2, row);
            inputs.AddRange([input, button]);
            button.Click += (_, _) => choose();
            input.TextChanged += (_, _) =>
            {
                pending = [];
                apply.Enabled = false;
                details.Clear();
                status.Text = "Verifique o esquema para atualizar a lista.";
            };
        }
    }

    private void PickFile(TextBox field, string filter)
    {
        using var picker = new OpenFileDialog { CheckFileExists = true, Filter = filter };
        if (picker.ShowDialog(this) == DialogResult.OK)
            field.Text = picker.FileName;
    }
}
