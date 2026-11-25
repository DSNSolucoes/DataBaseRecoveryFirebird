using RecuperadorFirebird.Application;
using RecuperadorFirebird.Domain;
using RecuperadorFirebird.Infrastructure;

namespace RecuperadorFirebird;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var files = new LocalWorkspaceFiles();
        var tools = new FirebirdProcessTools();
        var recover = new RecoverDatabase(files, tools);
        var metadata = new GenerateMetadataScript(files);
        var completion = new CompleteDatabaseSchema(files, tools);
        var repair = new RepairDatabase(files, tools);
        if (args.Length > 0 && args[0] == "--reparar")
        {
            if (args.Length is not (5 or 6) || !int.TryParse(args[4], out var level) ||
                (args.Length == 6 && args[5] != "--aceitar-risco"))
                return 64;
            try
            {
                var request = new RepairRequest(args[1], args[2], args[3], (RepairLevel)level, args.Length == 6);
                var result = repair.ExecuteAsync(request, Console.WriteLine).GetAwaiter().GetResult();
                Console.WriteLine(result.Message);
                return result.Outcome switch
                {
                    RepairOutcome.DiagnosticClean or RepairOutcome.RestoredAndValidated => 0,
                    RepairOutcome.DiagnosticIssues or RepairOutcome.RestoredWithWarnings => 2,
                    _ => 1
                };
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
        if (args.Length > 0 && args[0] is "--verificar-esquema" or "--completar-esquema")
        {
            if (args.Length != 4)
                return 64;
            try
            {
                var request = new SchemaCompletionRequest(args[1], args[2], args[3]);
                var missing = completion.AnalyzeAsync(request).GetAwaiter().GetResult();
                foreach (var change in missing.DistinctBy(item => (item.Kind, item.Name)))
                    Console.WriteLine($"{change.Kind}: {change.Name}" +
                        (change.BlockedReason is null ? "" : $" [bloqueado: {change.BlockedReason}]"));
                if (args[0] == "--completar-esquema")
                    missing = completion.ApplyAsync(request, missing).GetAwaiter().GetResult();
                return missing.Count == 0 ? 0 : 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
        if (args.Length > 0 && args[0] == "--gerar-metadados")
        {
            if (args.Length is < 3 or > 5)
                return 64;
            try
            {
                var selection = new SchemaSelection(ExcludedNames.Parse(args.Length >= 4 ? args[3] : ""),
                    ExcludedNames.Parse(args.Length == 5 ? args[4] : ""));
                var result = metadata.ExecuteAsync(args[1], args[2], selection).GetAwaiter().GetResult();
                Console.WriteLine(result);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
        if (args.Length > 0 && args[0] == "--recuperar")
        {
            if (args.Length is not (4 or 5))
                return 64;
            try
            {
                var options = new RecoveryOptions(args[1], args[2], args[3], ExcludedNames.Parse(args.Length == 5 ? args[4] : ""));
                var result = recover.ExecuteAsync(options, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                return result.Verified ? 0 : 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new MainForm(recover, metadata, completion, repair, FirebirdProcessTools.FindInstallation()));
        return 0;
    }
}
