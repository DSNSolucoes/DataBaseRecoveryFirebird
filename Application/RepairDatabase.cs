using System.Text.RegularExpressions;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Application;

public sealed record RepairRequest(string Source, string Destination, string Firebird, RepairLevel Level, bool AcceptRisk = false);
public sealed record RepairResult(string Folder, RepairOutcome Outcome, string? RestoredDatabase, string Message);

public sealed class RepairDatabase(IWorkspaceFiles files, IFirebirdTools tools)
{
    public async Task<RepairResult> ExecuteAsync(RepairRequest request, Action<string> progress, CancellationToken cancellation = default)
    {
        if (!Enum.IsDefined(request.Level))
            throw new ArgumentOutOfRangeException(nameof(request), "Selecione um nível de reparação válido.");
        if (request.Level >= RepairLevel.PageRepair && !request.AcceptRisk)
            throw new InvalidOperationException("Confirme o risco do nível selecionado antes de continuar.");
        var source = Path.GetFullPath(request.Source);
        if (!files.FileExists(source))
            throw new FileNotFoundException("Selecione um banco Firebird existente.");
        if (!files.DirectoryExists(request.Destination))
            throw new DirectoryNotFoundException("Selecione uma pasta de saída existente.");
        tools.ValidateInstallation(request.Firebird);
        if (request.Level != RepairLevel.Diagnostic && !files.FileExists(Path.Combine(request.Firebird, "gbak.exe")))
            throw new FileNotFoundException("A pasta do Firebird precisa conter gbak.exe.");

        var runId = Guid.NewGuid().ToString("N")[..8];
        var folder = Path.Combine(Path.GetFullPath(request.Destination), $"reparacao-{DateTime.Now:yyyyMMdd-HHmmss}-{runId}");
        files.CreateDirectory(folder);
        var working = Path.Combine(folder, "copia-de-trabalho.fdb");
        var backup = Path.Combine(folder, "backup.fbk");
        var restored = Path.Combine(folder, "banco-restaurado.fdb");
        string? completedRestore = null;
        progress($"Pasta da execução: {folder}");

        async Task<FirebirdCommandResult> Run(string tool, string log, params string[] args)
        {
            progress($"{tool} {string.Join(" ", args.TakeWhile(arg => arg != "-user" && arg != working))} · {log}");
            return await tools.RunAsync(request.Firebird, tool, args, Path.Combine(folder, log), null, cancellation);
        }

        async Task<RepairResult> Finish(RepairOutcome outcome, string message)
        {
            var risk = request.Level >= RepairLevel.PageRepair
                ? "Este nível pode perder informações na cópia e no banco restaurado."
                : "Este nível não usa opções deliberadas de descarte de registros.";
            await files.WriteTextAsync(Path.Combine(folder, "resumo.txt"),
                $"Nível: {(int)request.Level}\nOrigem: {source}\nResultado: {message}\n" +
                $"Banco restaurado: {completedRestore ?? "(nenhum confirmado)"}\n{risk}\n" +
                "A validação estrutural não confirma que todos os dados da origem estavam legíveis ou foram preservados.\n");
            return new RepairResult(folder, outcome, completedRestore, message);
        }

        try
        {
            progress("Copiando a origem com proteção contra gravações concorrentes...");
            await files.CopyExclusiveAsync(source, working, cancellation);
            var header = await Run("gstat", "01-cabecalho.log", "-h", working);
            if (!header.Ok || !IsIsolatedHeader(header.Output))
                return await Finish(RepairOutcome.Failed,
                    "Não foi possível confirmar que a cópia é um banco de arquivo único, sem sombras ou arquivo de diferenças. Consulte 01-cabecalho.log.");

            if (request.Level == RepairLevel.Diagnostic)
            {
                var diagnosis = await Run("gfix", "02-diagnostico.log", "-validate", "-full", "-no_update", "-user", "SYSDBA", working);
                return IsClean(diagnosis)
                    ? await Finish(RepairOutcome.DiagnosticClean, "Diagnóstico sem problemas reportados.")
                    : await Finish(RepairOutcome.DiagnosticIssues, "O diagnóstico registrou problemas ou não pôde ser concluído. Consulte 02-diagnostico.log.");
            }

            var ignore = request.Level == RepairLevel.IgnoreChecksums;
            if (request.Level >= RepairLevel.PageRepair)
            {
                var flags = new List<string> { "-validate" };
                if (request.Level >= RepairLevel.Mend)
                    flags.Add("-full");
                if (ignore)
                    flags.Add("-ignore");
                flags.AddRange(["-user", "SYSDBA", working]);
                var validation = await Run("gfix", "02-reparo-estrutural.log", flags.ToArray());
                if (!validation.Ok && request.Level == RepairLevel.PageRepair)
                    return await Finish(RepairOutcome.Failed, "A reparação de páginas falhou. Consulte 02-reparo-estrutural.log.");
                if (!IsClean(validation) && request.Level >= RepairLevel.Mend)
                    progress("A validação registrou problemas. Continuando com o mend, conforme o nível autorizado.");
            }

            if (request.Level >= RepairLevel.Mend)
            {
                string[] flags = ignore ? ["-mend", "-ignore"] : ["-mend"];
                var mend = await Run("gfix", "03-mend.log", [.. flags, "-user", "SYSDBA", working]);
                if (!mend.Ok)
                    return await Finish(RepairOutcome.Failed, "O mend falhou. Consulte 03-mend.log.");
            }

            var backupFlags = new List<string> { "-backup", "-garbage_collect", "-nodbtriggers", "-verify" };
            if (ignore)
                backupFlags.Add("-ignore");
            var backedUp = await Run("gbak", "04-backup.log", [.. backupFlags, "-user", "SYSDBA", working, backup]);
            if (!backedUp.Ok || !files.FileExists(backup))
                return await Finish(RepairOutcome.Failed, "O backup não foi concluído. Consulte 04-backup.log.");

            var restoredResult = await Run("gbak", "05-restauracao.log", "-create", "-kill", "-verify", "-user", "SYSDBA", backup, restored);
            if (!restoredResult.Ok || !files.FileExists(restored))
                return await Finish(RepairOutcome.Failed, "A restauração não foi concluída. Consulte 05-restauracao.log.");
            completedRestore = restored;

            var final = await Run("gfix", "06-validacao-final.log", "-validate", "-full", "-no_update", "-user", "SYSDBA", restored);
            return IsClean(final)
                ? await Finish(RepairOutcome.RestoredAndValidated, "Banco restaurado. A validação estrutural não reportou problemas.")
                : await Finish(RepairOutcome.RestoredWithWarnings, "Banco restaurado com pendências na validação. Consulte 06-validacao-final.log.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return await Finish(RepairOutcome.Cancelled, "Execução cancelada. A pasta pode conter arquivos parciais.");
        }
        catch (Exception error)
        {
            return await Finish(RepairOutcome.Failed, $"Execução interrompida: {error.Message}");
        }
    }

    private static bool IsClean(FirebirdCommandResult result) => result.Ok && string.IsNullOrWhiteSpace(result.Output);

    private static bool IsIsolatedHeader(string header) =>
        Regex.IsMatch(header, @"(?im)^\s*Shadow count\s+0\s*$") &&
        Regex.IsMatch(header, @"(?im)^\s*Next header page\s+0\s*$") &&
        Regex.IsMatch(header, @"(?im)^\s*\*END\*\s*$") &&
        !Regex.IsMatch(header, @"(?im)^\s*(?:Continuation file|Difference file|Last logical page)\s*:|^\s*Attributes[^\r\n]*(?:backup|stalled)");
}
