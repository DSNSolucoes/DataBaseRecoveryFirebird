using System.Diagnostics;
using RecuperadorFirebird.Application;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird;

internal sealed class MainForm : Form
{
    private readonly TextBox source = new() { PlaceholderText = "Arquivo .fdb, .foc ou outro banco Firebird" };
    private readonly TextBox destination = new() { PlaceholderText = "Pasta para o banco e os relatórios" };
    private readonly TextBox firebird = new();
    private readonly TextBox excluded = new() { PlaceholderText = "Opcional. Separe os nomes por vírgula." };
    private readonly TextBox activity = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, AccessibleName = "Atividade da recuperação"
    };
    private readonly Label status = new() { AutoSize = true, Text = "Aguardando seleção" };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill };
    private readonly Button start = new() { Text = "&Iniciar recuperação", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly Button stop = new() { Text = "&Cancelar", AutoSize = true, Enabled = false, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button open = new() { Text = "&Abrir pasta do resultado", AutoSize = true, Enabled = false, Padding = new Padding(8, 4, 8, 4) };
    private readonly List<Control> inputs = [];
    private readonly RecoverDatabase recoverDatabase;
    private CancellationTokenSource? cancellation;
    private string? runFolder;

    public MainForm(RecoverDatabase recoverDatabase, GenerateMetadataScript metadata, CompleteDatabaseSchema completion, string firebirdInstallation)
    {
        this.recoverDatabase = recoverDatabase;
        firebird.Text = firebirdInstallation;
        Text = "Recuperador Firebird 5";
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(850, 630);
        MinimumSize = new Size(675, 580);
        BackColor = Color.FromArgb(246, 248, 250);
        ForeColor = Color.FromArgb(22, 34, 50);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 16), ColumnCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 163));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var recoveryTab = new TabPage("Recuperar dados") { BackColor = BackColor };
        var metadataTab = new TabPage("Gerar script de metadados") { BackColor = BackColor };
        var completionTab = new TabPage("Completar esquema") { BackColor = BackColor };
        recoveryTab.Controls.Add(layout);
        metadataTab.Controls.Add(new MetadataPage(metadata));
        var schemaPage = new SchemaCompletionPage(completion, firebirdInstallation);
        completionTab.Controls.Add(schemaPage);
        tabs.TabPages.AddRange([recoveryTab, metadataTab, completionTab]);
        FormClosing += (_, args) => { if (schemaPage.IsBusy) args.Cancel = true; };
        Controls.Add(tabs);
        AddWide(layout, new Label
        {
            Text = Text, AutoSize = true, Font = new Font(Font.FontFamily, 23, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        });
        AddWide(layout, new Label
        {
            Text = "Copie as tabelas legíveis para um banco novo e preserve o arquivo original.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 16)
        });
        AddField(layout, "Banco de &origem", source, "Selecionar arquivo", PickSource);
        AddField(layout, "&Pasta de saída", destination, "Selecionar pasta", () => PickFolder(destination, "Pasta de saída"));
        AddField(layout, "Ferramentas do &Firebird", firebird, "Selecionar pasta", () => PickFolder(firebird, "Pasta do Firebird 5"));
        AddField(layout, "&Tabelas a excluir", excluded);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 12), WrapContents = false };
        start.BackColor = Color.FromArgb(15, 76, 107);
        start.ForeColor = Color.White;
        start.FlatStyle = FlatStyle.Flat;
        start.FlatAppearance.BorderSize = 0;
        buttons.Controls.AddRange([start, stop, open]);
        AddWide(layout, buttons);
        AddWide(layout, status);
        bar.Margin = new Padding(0, 4, 0, 10);
        AddWide(layout, bar, new RowStyle(SizeType.Absolute, 24));
        AddWide(layout, new Label { AutoSize = true, Text = "Atividade", Margin = new Padding(0, 0, 0, 4) });
        activity.Text = "Os detalhes da execução aparecerão aqui.";
        AddWide(layout, activity, new RowStyle(SizeType.Percent, 100));
        start.Click += async (_, _) => await Recover();
        stop.Click += (_, _) => Cancel();
        open.Click += (_, _) =>
        {
            if (runFolder is not null)
                Process.Start(new ProcessStartInfo(runFolder) { UseShellExecute = true });
        };
        FormClosing += (_, args) =>
        {
            if (cancellation is null)
                return;
            args.Cancel = true;
            Cancel();
        };
        AcceptButton = start;
        tabs.SelectedIndexChanged += (_, _) => AcceptButton = tabs.SelectedIndex == 0 ? start : null;
    }

    private void Cancel()
    {
        stop.Enabled = false;
        status.Text = "Cancelando...";
        cancellation?.Cancel();
    }

    private void AddField(TableLayoutPanel layout, string title, TextBox box, string? buttonText = null, Action? action = null)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        layout.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        box.AccessibleName = title.Replace("&", "");
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        layout.Controls.Add(box, 1, row);
        inputs.Add(box);
        if (action is null)
        {
            layout.SetColumnSpan(box, 2);
            return;
        }
        var button = new Button { Text = buttonText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0) };
        button.Click += (_, _) => action();
        layout.Controls.Add(button, 2, row);
        inputs.Add(button);
    }

    private static void AddWide(TableLayoutPanel layout, Control control, RowStyle? style = null)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(style ?? new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 3);
    }

    private void PickSource()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Selecionar o banco de origem", CheckFileExists = true,
            Filter = "Bancos Firebird (*.fdb;*.foc;*.gdb)|*.fdb;*.foc;*.gdb|Todos os arquivos (*.*)|*.*"
        };
        if (picker.ShowDialog(this) == DialogResult.OK)
            source.Text = picker.FileName;
    }

    private void PickFolder(TextBox box, string description)
    {
        using var picker = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        if (Directory.Exists(box.Text))
            picker.SelectedPath = box.Text;
        if (picker.ShowDialog(this) == DialogResult.OK)
            box.Text = picker.SelectedPath;
    }

    private async Task Recover()
    {
        var options = new RecoveryOptions(source.Text.Trim(), destination.Text.Trim(), firebird.Text.Trim(), ExcludedNames.Parse(excluded.Text));
        var invalid = !File.Exists(options.Source) ? (source, "Selecione o arquivo do banco de origem.")
            : !Directory.Exists(options.Destination) ? (destination, "Selecione uma pasta de saída existente.")
            : !Directory.Exists(options.Firebird) ? (firebird, "Selecione a pasta do Firebird 5.") : default;
        if (invalid.Item1 is not null)
        {
            status.Text = invalid.Item2;
            invalid.Item1.Focus();
            return;
        }

        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource;
        activity.Clear();
        runFolder = null;
        status.Text = "Recuperação em andamento";
        bar.Style = ProgressBarStyle.Marquee;
        start.Enabled = open.Enabled = false;
        stop.Enabled = true;
        foreach (var control in inputs)
            control.Enabled = false;
        const string prefix = "Arquivos da execução em ";
        IProgress<string> reporter = new Progress<string>(message =>
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                runFolder = message[prefix.Length..];
            Append(message);
        });
        try
        {
            var result = await Task.Run(() => recoverDatabase.ExecuteAsync(options, reporter.Report, tokenSource.Token));
            status.Text = result.Verified ? "Concluído — contagens conferidas" : "Concluído — revise as divergências";
            Append($"{result.Tables:N0} tabelas · {result.Rows:N0} registros · {result.Unrecovered:N0} tabelas sem conferência.");
            Append("Consulte resumo.txt para os limites da recuperação.");
        }
        catch (OperationCanceledException)
        {
            status.Text = "Recuperação cancelada";
            Append("Os arquivos parciais estão na pasta da execução.");
        }
        catch (Exception exception)
        {
            status.Text = "Recuperação interrompida";
            Append(exception.Message);
        }
        finally
        {
            cancellation = null;
            bar.Style = ProgressBarStyle.Continuous;
            stop.Enabled = false;
            start.Enabled = true;
            open.Enabled = runFolder is not null && Directory.Exists(runFolder);
            foreach (var control in inputs)
                control.Enabled = true;
        }
    }

    private void Append(string message) => activity.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
}
