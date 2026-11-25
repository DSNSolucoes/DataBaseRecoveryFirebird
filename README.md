# Recuperador Firebird 5

Aplicativo C# para Windows Forms. A publicação é para Windows x64 e inclui o runtime .NET. Instale ou selecione uma distribuição local do Firebird 5 que contenha `isql.exe`, `gfix.exe`, `gstat.exe`, `fbsvcmgr.exe` e o mecanismo embarcado.

## Uso

Consulte o [Manual de uso](MANUAL.md) para seguir o passo a passo, entender os arquivos gerados e diagnosticar falhas.

A aba **Completar esquema** compara um SQL de metadados com o banco recuperado e permite criar os objetos faltantes após revisão.

A aba **Reparar banco** oferece cinco níveis, do diagnóstico à tentativa com `mend` e checksums ignorados. Cada execução trabalha em uma cópia e guarda os logs numa pasta própria.

1. Abra `PublicacaoEsquema/RecuperadorFirebird.exe`.
2. Selecione o arquivo de origem e uma pasta de saída local com espaço para a cópia da origem e o banco novo. Feche conexões que estejam gravando no arquivo de origem.
3. Confira a pasta da instalação Firebird. Preencha **Tabelas a excluir** apenas para omitir tabelas específicas.
4. Clique em **Iniciar recuperação**. Use **Abrir pasta do resultado** para consultar o banco e os relatórios.

Cada execução cria uma pasta própria. Ela contém `banco-recuperado.fdb`, a cópia de trabalho em modo somente leitura, o cabeçalho, o esquema extraído, os scripts SQL, os logs das ferramentas, `conferencia-dados.csv` e `resumo.txt`. A recuperação usa conexões locais pelo mecanismo embarcado com o usuário SYSDBA.

A conferência compara as linhas que o Firebird conseguiu consultar e transferir com as linhas presentes no banco novo. Uma origem corrompida pode ocultar dados do catálogo e registros. Gatilhos, visões, procedimentos, permissões, índices secundários e restrições fora das definições recriadas exigem revisão antes de colocar o banco novo em produção.

## Compilação

Use o SDK .NET 10 para compilar e publicar.

```powershell
dotnet build RecuperadorFirebird.slnx -c Release
dotnet test RecuperadorFirebird.slnx -c Release
dotnet publish RecuperadorFirebird.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o PublicacaoEsquema
```

O mesmo núcleo pode ser executado em lote para testes e diagnóstico.

```powershell
dotnet run --project RecuperadorFirebird.csproj -c Release -- --recuperar "C:\Bancos\banco.foc" "C:\Recuperados" "C:\Program Files\Firebird\Firebird_5_0" "TEF_ARQ,LOGACESSOS"
```

O último argumento contém exclusões opcionais. O código de saída é `0` quando as contagens e a validação estrutural passam, `2` quando há divergências ou erros de validação, `1` quando a execução é interrompida e `64` quando os argumentos estão incompletos.

## Gerar e aplicar metadados

Na aba **Gerar script de metadados**, selecione o SQL exportado e o arquivo de saída. Use as exclusões opcionais para tabelas ou objetos incompatíveis com o banco de destino. Os nomes excluídos aparecem nos comentários do script.

O gerador lê chaves declaradas em `ALTER TABLE` e dentro de `CREATE TABLE`, e preserva os delimitadores necessários de PSQL. O arquivo gerado contém somente a aplicação de PKs, FKs, gatilhos, procedimentos, visões e exceções. As tabelas, colunas, domínios, sequências e chaves únicas exigidas pelas FKs devem existir no banco restaurado. Chaves e gatilhos de tabelas ausentes são ignorados. PKs já existentes e FKs com o mesmo nome são preservadas.

Execute o arquivo manualmente no **isql** do Firebird 5, conectado ao banco restaurado. O script faz o commit ao final e interrompe a execução em caso de erro. Se executar em uma sessão que permaneça aberta após um erro, faça `ROLLBACK` antes de tentar novamente.

```powershell
isql -bail -nodbtriggers -ch UTF8 -user SYSDBA -i "C:\Recuperados\aplicar-metadados.sql" "C:\Recuperados\banco-recuperado.fdb"
```

Para gerar pelo terminal, informe o SQL de origem, o SQL de saída, as tabelas excluídas e os objetos excluídos. Os dois últimos argumentos são opcionais.

```powershell
dotnet run --project RecuperadorFirebird.csproj -c Release -- --gerar-metadados meta.sql aplicar-metadados.sql "TABELA_OMITIDA" "PROCEDURE_INCOMPATIVEL"
```

## Arquitetura

- **Domain** modela os nomes Firebird, as exclusões de tabelas e objetos e a conferência dos registros transferidos. Ele não conhece WinForms, o sistema de arquivos ou os executáveis do Firebird.
- **Application** orquestra os casos de uso de recuperar banco e gerar SQL. As interfaces definidas aqui permitem trocar os acessos a arquivos e o executor das ferramentas.
- **Infrastructure** implementa os acessos a arquivos e a execução dos utilitários. O ponto de entrada do **WinForms** conecta os adaptadores aos casos de uso. A linha de comando usa os mesmos casos de uso que as duas abas.
- **Tests** verifica as regras de domínio, a geração de metadados e o fluxo de recuperação com arquivos e ferramentas em memória. Os testes unitários rodam sem uma instalação do Firebird.
