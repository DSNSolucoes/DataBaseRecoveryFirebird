# Manual de uso do Recuperador Firebird 5

O programa tem duas abas. **Recuperar dados** copia as tabelas legíveis para um banco novo. **Gerar script de metadados** extrai objetos de um arquivo SQL para você aplicar depois.

## 1. Preparar o ambiente

- Use Windows x64 e uma instalação local do Firebird 5 com o mecanismo embarcado e os utilitários `isql.exe`, `gfix.exe`, `gstat.exe` e `fbsvcmgr.exe`.
- Se já tiver o executável publicado, abra `RecuperadorFirebird.exe`. Se baixou o código do GitHub, siga os [passos de compilação e publicação](README.md#compilação) para gerar o executável na pasta `PublicacaoRefatorada`.
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

## 4. Resolver problemas comuns

| Situação | O que conferir |
| --- | --- |
| O programa pede a pasta do Firebird | Selecione a instalação do Firebird 5 com os quatro utilitários exigidos. |
| O arquivo está em uso | Feche as conexões que gravam na origem e tente novamente. |
| A extração de esquema ficou parcial | Confira `extracao-esquema.log`, as estruturas complementares e o relatório por tabela. |
| A recuperação exibiu divergências | Abra o CSV, localize as linhas marcadas **NÃO** e consulte os logs de cópia e validação. |
| O SQL falhou por dependência ausente | Confira o nome indicado pelo `isql` e compare o esquema do destino com a origem dos metadados. Gere outro script após corrigir a dependência ou excluir o objeto, conforme o caso. |
