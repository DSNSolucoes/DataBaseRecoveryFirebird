# Manual de uso do Recuperador Firebird 5

**Recuperar dados** copia as tabelas legíveis para um banco novo. **Gerar script de metadados** extrai objetos para aplicação manual. **Completar esquema** compara o SQL de metadados com o catálogo do banco. **Reparar banco** oferece níveis de diagnóstico e reparação em uma cópia.

## 1. Preparar o ambiente

- Use Windows x64 e uma instalação local do Firebird 5 com o mecanismo embarcado e os utilitários `isql.exe`, `gfix.exe`, `gstat.exe` e `fbsvcmgr.exe`.
- Se já tiver o executável publicado, abra `RecuperadorFirebird.exe`. Se baixou o código do GitHub, siga os [passos de compilação e publicação](README.md#compilação) para gerar o executável na pasta `PublicacaoEsquema`.
- Feche o aplicativo que usa o banco de origem e qualquer conexão que esteja gravando nele. Escolha uma pasta local com espaço para a cópia da origem, o banco novo e os relatórios.

## 2. Recuperar dados

1. Abra a aba **Recuperar dados**.
2. Em **Banco de origem**, clique em **Selecionar arquivo** e escolha o banco Firebird.
3. Em **Pasta de saída**, clique em **Selecionar pasta**. Cada execução cria uma subpasta `recuperacao-...`.
4. Em **Ferramentas do Firebird**, confira a pasta da instalação. Se estiver vazia, selecione a pasta que contém os utilitários.
5. Deixe **Tabelas a excluir** vazio para tentar copiar todas as tabelas encontradas. Para omitir tabelas específicas, informe os nomes separados por vírgula.
6. Clique em **Iniciar recuperação** e acompanhe a seção **Atividade**. Ao final, use **Abrir pasta do resultado**.

O programa trabalha em uma cópia em modo somente leitura e grava os registros em um banco novo. Se cancelar, a pasta da execução pode conter arquivos parciais. Use uma nova execução para tentar novamente.

### Conferir o resultado

| Arquivo | Como usar |
| --- | --- |
| `banco-recuperado.fdb` | Banco novo com os dados transferidos. |
| `origem-somente-leitura.fdb` | Cópia de trabalho da origem usada na execução. |
| `resumo.txt` | Total de tabelas e registros, exclusões e resultado da validação. |
| `conferencia-dados.csv` | Compare **Transferidos** com **NoBancoNovo**. **SIM** indica que ambas as contagens existem e coincidem. Um campo vazio significa que a contagem não foi obtida. |
| `extracao-esquema.log` e `copiar-dados.log` | Consulte para identificar falhas na leitura da estrutura ou na transferência. |
| `validacao-banco-novo.log` | Saída da validação estrutural pelo `gfix`. Uma validação sem erros pode deixar o arquivo vazio. |

**Contagens conferidas** confirma os registros transferidos e a validação estrutural do banco novo. A corrupção pode ocultar tabelas ou registros na origem. Antes de usar o banco em produção, confira dados importantes no sistema e verifique os objetos de metadados de que ele depende.

## 3. Gerar e aplicar metadados

1. Abra a aba **Gerar script de metadados**.
2. Em **Arquivo de metadados**, selecione o `meta.sql` ou outro SQL exportado.
3. Em **Arquivo de saída**, escolha onde salvar o novo script. O nome sugerido é `aplicar-metadados.sql`.
4. Use **Tabelas excluídas** ou **Objetos excluídos** para omitir nomes específicos, separados por vírgula. Confira as exclusões no cabeçalho do SQL gerado.
5. Clique em **Gerar script SQL** e confira o resumo exibido na aba.

O script contém chaves primárias e estrangeiras, gatilhos, procedimentos, visões e exceções encontrados no arquivo. Revise as dependências indicadas no cabeçalho. As tabelas, colunas, domínios, sequências e demais dependências devem existir no destino. Uma referência a uma exceção no corpo de um gatilho ou procedimento exige a definição dessa exceção.

Teste o SQL primeiro em uma cópia do banco recuperado. No PowerShell, ajuste os três caminhos do exemplo para a sua instalação, o script gerado e o **banco de destino**:

```powershell
& 'C:\Program Files\Firebird\Firebird_5_0\isql.exe' -bail -nodbtriggers -ch UTF8 -user SYSDBA -i 'C:\Recuperados\aplicar-metadados.sql' 'C:\Recuperados\banco-teste.fdb'
```

O script faz o commit ao final. Se ocorrer um erro, leia a mensagem antes de tentar novamente. Se a sessão do `isql` continuar aberta, execute `ROLLBACK;` antes de corrigir e repetir a aplicação.

## 4. Completar o esquema do banco recuperado

1. Abra a aba **Completar esquema** e selecione o `meta.sql` e uma cópia do banco recuperado.
2. Confira a pasta em **Ferramentas do Firebird** e clique em **Verificar esquema**.
3. Revise as tabelas, os campos, as PKs, FKs, restrições únicas, índices, visões, gatilhos, procedimentos e exceções listados.
4. Clique em **Criar objetos faltantes** e confirme o caminho do banco. O programa reaplica a verificação e mostra o que ainda estiver pendente.

A verificação compara a presença dos objetos e preserva os existentes. Tabelas recriadas começam vazias, e campos novos usam a definição do SQL. Rotinas que referenciam exceções ausentes aparecem como bloqueadas. Quando o Firebird rejeita outro comando do lote, por exemplo por uma dependência ausente ou por dados incompatíveis com uma restrição, o lote é interrompido e a mensagem aparece na aba.

## 5. Reparar um banco por nível

1. Abra **Reparar banco** e selecione o banco de origem, a pasta de saída e a instalação do Firebird 5. Os níveis de reconstrução também exigem `gbak.exe`. Feche os programas que gravam na origem antes da cópia.
2. Escolha um nível. Comece pelo diagnóstico e passe para outro nível se necessário. Cada tentativa cria uma pasta `reparacao-...` e usa uma nova cópia do arquivo selecionado.
3. Clique em **Executar nível**. Para os níveis 3 a 5, leia a confirmação sobre risco de perda no resultado.
4. Abra a pasta do resultado. Consulte `resumo.txt` e os logs numerados. Nos níveis 2 a 5, uma restauração bem-sucedida gera `backup.fbk` e `banco-restaurado.fdb`.

| Nível | Procedimento | Efeito |
| --- | --- | --- |
| **1 · Básico** | `gfix -validate -full -no_update` | Diagnóstico de páginas e registros sem reparação. |
| **2 · Médio** | `gbak -backup -garbage_collect -nodbtriggers`, restauração em arquivo novo e validação sem reparação | Reconstrói a partir dos dados que o backup consegue ler, com as verificações de integridade habilitadas. |
| **3 · Estrutural** | `gfix -validate` e reconstrução do nível 2 | Pode alterar estruturas e afetar informações danificadas na cópia. |
| **4 · Avançado** | `gfix -validate -full`, `gfix -mend` e reconstrução | O backup pode omitir registros danificados. |
| **5 · Último recurso** | `-ignore` na validação completa, no `mend` e no backup, seguido de restauração e validação | Pode aceitar páginas danificadas ou omitir registros. |

O arquivo original é preservado. A validação final verifica a estrutura do banco restaurado. Confira os dados importantes antes de colocá-lo em uso. Esta aba foi feita para bancos de arquivo único, sem arquivos de sombra ou de diferenças. Arquivos parciais de uma tentativa interrompida devem ser avaliados junto do resumo e dos logs.

Referências oficiais do Firebird. [Validação e recuperação com gfix](https://firebirdsql.org/file/documentation/html/en/firebirddocs/gfix/firebird-gfix.html) · [Backup e restauração com gbak](https://firebirdsql.org/file/documentation/html/en/firebirddocs/gbak/firebird-gbak.html)

## 6. Resolver problemas comuns

| Situação | O que conferir |
| --- | --- |
| O programa pede a pasta do Firebird | Selecione a instalação do Firebird 5 com os quatro utilitários exigidos. |
| O arquivo está em uso | Feche as conexões que gravam na origem e tente novamente. |
| A extração de esquema ficou parcial | Confira `extracao-esquema.log`, as estruturas complementares e o relatório por tabela. |
| A recuperação exibiu divergências | Abra o CSV, localize as linhas marcadas **NÃO** e consulte os logs de cópia e validação. |
| O SQL falhou por dependência ausente | Confira o nome indicado pelo `isql` e compare o esquema do destino com a origem dos metadados. Gere outro script após corrigir a dependência ou excluir o objeto, conforme o caso. |
