using RecuperadorFirebird.Application;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird;

internal sealed class MetadataPage : UserControl
{
    private readonly TextBox source = new() { PlaceholderText = "Selecione o arquivo meta.sql", AccessibleName = "Arquivo de metadados" };
    private readonly TextBox output = new() { PlaceholderText = "Escolha onde salvar o arquivo SQL", AccessibleName = "Arquivo SQL de saída" };
    private readonly TextBox exclusions = new() { PlaceholderText = "Opcional. Separe os nomes por vírgula.", AccessibleName = "Tabelas excluídas" };
    private readonly TextBox excludedObjects = new() { PlaceholderText = "Opcional. Separe os nomes por vírgula.", AccessibleName = "Objetos excluídos" };
    private readonly Label result = new() { AutoSize = true, Text = "Aguardando seleção" };
    private readonly Button generate = new() { Text = "&Gerar script SQL", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly GenerateMetadataScript generateMetadata;

    public MetadataPage(GenerateMetadataScript generateMetadata)
    {
        this.generateMetadata = generateMetadata;
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 16), ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 163));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(layout);
        AddWide(new Label
        {
            Text = "Script de metadados", AutoSize = true, Font = new Font("Segoe UI", 23, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        });
        AddWide(new Label
        {
            Text = "Extraia PKs, FKs, gatilhos, procedimentos, visões e exceções.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 16)
        });
        AddField("Arquivo de &metadados", source, "Selecionar arquivo", () =>
        {
            using var picker = new OpenFileDialog { Title = "Selecionar o arquivo de metadados", Filter = "Scripts SQL (*.sql)|*.sql|Todos os arquivos (*.*)|*.*" };
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            source.Text = picker.FileName;
            if (string.IsNullOrWhiteSpace(output.Text))
                output.Text = Path.Combine(Path.GetDirectoryName(source.Text)!, "aplicar-metadados.sql");
        });
        AddField("Arquivo de &saída", output, "Salvar como", () =>
        {
            using var picker = new SaveFileDialog
            {
                Title = "Salvar o script gerado", Filter = "Scripts SQL (*.sql)|*.sql", DefaultExt = "sql", OverwritePrompt = false,
                FileName = Path.GetFileName(output.Text.Length == 0 ? "aplicar-metadados.sql" : output.Text)
            };
            if (picker.ShowDialog(this) == DialogResult.OK)
                output.Text = picker.FileName;
        });
        AddField("Tabelas &excluídas", exclusions);
        AddField("&Objetos excluídos", excludedObjects);
        generate.BackColor = Color.FromArgb(15, 76, 107);
        generate.ForeColor = Color.White;
        generate.FlatStyle = FlatStyle.Flat;
        generate.FlatAppearance.BorderSize = 0;
        generate.Margin = new Padding(0, 14, 0, 12);
        generate.Anchor = AnchorStyles.Left;
        AddWide(generate);
        AddWide(result);
        AddWide(new Label
        {
            Text = "Execute o SQL manualmente no banco restaurado com o isql do Firebird 5.",
            AutoSize = true, Margin = new Padding(0, 14, 0, 4)
        });
        generate.Click += async (_, _) => await Generate();

        void AddWide(Control control)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 3);
        }

        void AddField(string title, TextBox field, string? buttonText = null, Action? action = null)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            layout.Controls.Add(field, 1, row);
            if (action is null)
            {
                layout.SetColumnSpan(field, 2);
                return;
            }
            var button = new Button { Text = buttonText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0) };
            button.Click += (_, _) => action();
            layout.Controls.Add(button, 2, row);
        }
    }

    private async Task Generate()
    {
        var inputPath = source.Text.Trim();
        var outputPath = output.Text.Trim();
        if (!File.Exists(inputPath) || outputPath.Length == 0)
        {
            result.Text = "Selecione os arquivos de entrada e de saída.";
            return;
        }
        if (File.Exists(outputPath) &&
            MessageBox.Show(this, "Substituir o arquivo SQL de saída?", "Gerar script", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        var selection = new SchemaSelection(ExcludedNames.Parse(exclusions.Text), ExcludedNames.Parse(excludedObjects.Text));
        generate.Enabled = false;
        result.Text = "Lendo metadados...";
        try
        {
            var counts = await Task.Run(() => generateMetadata.ExecuteAsync(inputPath, outputPath, selection));
            result.Text = $"Script gerado. {counts.PrimaryKeys} PKs · {counts.ForeignKeys} FKs · {counts.Triggers} gatilhos\n"
                + $"{counts.Procedures} procedimentos · {counts.Views} visões · {counts.Exceptions} exceções · {counts.Omitted} exclusões";
        }
        catch (Exception error)
        {
            result.Text = error.Message;
        }
        finally
        {
            generate.Enabled = true;
        }
    }
}
