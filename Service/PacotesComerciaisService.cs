using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Model.Gestao;
using Dapper;
using Npgsql;

namespace APIBack.Service;

public sealed record PacoteComercialDto(string Chave, string Nome, IReadOnlyList<string> Modulos,
    int MotoboysBase, int LojasAfetadas, decimal Preco, decimal? PrecoAnual, string? Cor, string? Icone, string? Frase, bool Arquivado);

/// <summary>Campos nulos ficam como estao. Mudar modulos exige confirmar quantas lojas recebem (LojasAfetadasConfirmadas).</summary>
public sealed record EditarPacoteComercialRequest(IReadOnlyList<string>? Modulos, int? LojasAfetadasConfirmadas,
    string? Nome = null, decimal? Preco = null, decimal? PrecoAnual = null, int? MotoboysBase = null,
    string? Cor = null, string? Icone = null, string? Frase = null);

public sealed record CriarPacoteComercialRequest(string? Nome, IReadOnlyList<string>? Modulos, decimal Preco, decimal? PrecoAnual,
    int MotoboysBase, string? Cor, string? Icone, string? Frase);

public sealed record ProdutoAvulsoDto(long Id, string Nome, decimal Preco, IReadOnlyList<string> Modulos, int Motoboys, string? Cor, bool Arquivado);
public sealed record SalvarProdutoAvulsoRequest(string? Nome, decimal Preco, IReadOnlyList<string>? Modulos, int Motoboys, string? Cor);

public interface IPacotesComerciaisService
{
    Task<IReadOnlyList<PacoteComercialDto>> ListarAsync(CancellationToken ct = default);
    Task<PacoteComercialDto> AtualizarAsync(string chave, EditarPacoteComercialRequest request, int usuarioId, CancellationToken ct = default);
    Task<PacoteComercialDto> CriarAsync(CriarPacoteComercialRequest request, int usuarioId, CancellationToken ct = default);
    Task<PacoteComercialDto> ArquivarAsync(string chave, bool arquivado, int usuarioId, CancellationToken ct = default);
    Task ExcluirAsync(string chave, CancellationToken ct = default);
    Task<IReadOnlyList<ProdutoAvulsoDto>> ListarAvulsosAsync(CancellationToken ct = default);
    Task<ProdutoAvulsoDto> SalvarAvulsoAsync(long? id, SalvarProdutoAvulsoRequest request, CancellationToken ct = default);
    Task ExcluirAvulsoAsync(long id, CancellationToken ct = default);
}

public sealed class PacotesComerciaisService(NpgsqlDataSource source) : IPacotesComerciaisService
{
    private static readonly HashSet<string> ModulosPermitidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pedidos", "Mapa", "Delivery", "iFood", "Impressao", "Remuneracao", "WhatsApp", "Clientes",
        "Cardapio", "CardapioWeb", "Inteligencia", "Financeiro", "Experience", "Simulador",
        "Servicos", "FAQ", "Disponibilidade"
    };

    private sealed class Row
    {
        public string Chave { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string[] ModulosAtivos { get; set; } = Array.Empty<string>();
        public int MotoboysBase { get; set; }
        public int LojasAfetadas { get; set; }
        public decimal Preco { get; set; }
        public decimal? PrecoAnual { get; set; }
        public string? Cor { get; set; }
        public string? Icone { get; set; }
        public string? Frase { get; set; }
        public bool Arquivado { get; set; }
    }

    private sealed class AvulsoRow
    {
        public long Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public decimal Preco { get; set; }
        public string[] ModulosAtivos { get; set; } = Array.Empty<string>();
        public int Motoboys { get; set; }
        public string? Cor { get; set; }
        public bool Arquivado { get; set; }
    }

    private const string SelectSql = @"
SELECT p.chave AS Chave, p.nome AS Nome, p.modulos_ativos::text[] AS ModulosAtivos,
       p.motoboys_base AS MotoboysBase, p.preco AS Preco, p.preco_anual AS PrecoAnual, p.cor AS Cor, p.icone AS Icone,
       p.frase AS Frase, p.arquivado AS Arquivado,
       (SELECT COUNT(*) FROM estabelecimentos e WHERE e.plano_comercial = p.chave) AS LojasAfetadas
  FROM pacotes_comerciais p";

    private const string AvulsoSql = @"
SELECT id AS Id, nome AS Nome, preco AS Preco, modulos_ativos::text[] AS ModulosAtivos, motoboys AS Motoboys, cor AS Cor, arquivado AS Arquivado
  FROM produtos_avulsos";

    public async Task<IReadOnlyList<PacoteComercialDto>> ListarAsync(CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(SelectSql + " ORDER BY p.arquivado, p.ordem, p.preco, p.nome;", cancellationToken: ct));
        return rows.Select(ToDto).ToArray();
    }

    private static string[] Modulos(IReadOnlyList<string>? modulos)
    {
        if (modulos == null || modulos.Count == 0 || modulos.Any(m => !ModulosPermitidos.Contains(m)))
            throw new SituacaoComercialException(422, "MODULOS_INVALIDOS", "Escolha módulos válidos para o pacote.");
        return EstabelecimentoModuleMapper.ToDatabaseModules(modulos, null);
    }

    private static void ValidarPreco(decimal? preco, decimal? anual, int? motoboys)
    {
        if (preco is < 0 || anual is < 0) throw new SituacaoComercialException(422, "PRECO_INVALIDO", "O preço não pode ser negativo.");
        if (motoboys is < 0 or > 50) throw new SituacaoComercialException(422, "MOTOBOYS_INVALIDOS", "Motoboys inclusos entre 0 e 50.");
    }

    public async Task<PacoteComercialDto> AtualizarAsync(string chave, EditarPacoteComercialRequest request, int usuarioId, CancellationToken ct = default)
    {
        chave = (chave ?? string.Empty).Trim().ToLowerInvariant();
        ValidarPreco(request.Preco, request.PrecoAnual, request.MotoboysBase);
        var modulos = request.Modulos == null ? null : Modulos(request.Modulos);
        if (modulos != null && request.LojasAfetadasConfirmadas is null or < 0)
            throw new SituacaoComercialException(409, "CONFIRMACAO_NECESSARIA", "Confirme quantas lojas usam este pacote antes de salvar.");

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var existe = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM pacotes_comerciais WHERE chave=@Chave FOR UPDATE);", new { Chave = chave }, tx, cancellationToken: ct));
        if (!existe) throw new SituacaoComercialException(404, "PACOTE_NAO_ENCONTRADO", "Pacote não encontrado.");

        // Nome, preco e cara do plano: so o catalogo muda. O valor cobrado de cada loja continua sendo o do contrato.
        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE pacotes_comerciais
   SET nome = COALESCE(NULLIF(TRIM(@Nome), ''), nome), preco = COALESCE(@Preco, preco), preco_anual = COALESCE(@PrecoAnual, preco_anual),
       motoboys_base = COALESCE(@MotoboysBase, motoboys_base), cor = COALESCE(@Cor, cor), icone = COALESCE(@Icone, icone),
       frase = COALESCE(@Frase, frase), atualizado_em = NOW(), atualizado_por = @UsuarioId
 WHERE chave = @Chave;",
            new { Chave = chave, request.Nome, request.Preco, request.PrecoAnual, request.MotoboysBase, request.Cor, request.Icone, request.Frase, UsuarioId = usuarioId },
            tx, cancellationToken: ct));

        if (modulos != null)
        {
            var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM estabelecimentos WHERE plano_comercial=@Chave ORDER BY id FOR UPDATE;", new { Chave = chave }, tx, cancellationToken: ct))).ToArray();
            if (ids.Length != request.LojasAfetadasConfirmadas)
                throw new SituacaoComercialException(409, "IMPACTO_ALTERADO", $"Agora {ids.Length} loja(s) usam este pacote. Confira o impacto e confirme novamente.");
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE pacotes_comerciais SET modulos_ativos=@Modulos::modulo_enum[] WHERE chave=@Chave;", new { Chave = chave, Modulos = modulos }, tx, cancellationToken: ct));
            if (ids.Length > 0)
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos SET modulos_ativos=@Modulos::modulo_enum[], data_atualizacao=NOW()
 WHERE id=ANY(@Ids) AND plano_comercial=@Chave;", new { Ids = ids, Chave = chave, Modulos = modulos }, tx, cancellationToken: ct));
        }

        var result = await connection.QuerySingleAsync<Row>(new CommandDefinition(SelectSql + " WHERE p.chave=@Chave;", new { Chave = chave }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return ToDto(result);
    }

    /// <summary>Chave a partir do nome ("Delivery Pro" vira "delivery_pro"); se ja existir, ganha um numero.</summary>
    private static string ChaveDe(string nome)
    {
        var semAcento = new string(nome.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        var chave = Regex.Replace(semAcento.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (chave.Length < 2) chave = "plano";
        return chave.Length > 24 ? chave[..24] : chave;
    }

    public async Task<PacoteComercialDto> CriarAsync(CriarPacoteComercialRequest request, int usuarioId, CancellationToken ct = default)
    {
        var nome = request.Nome?.Trim();
        if (string.IsNullOrEmpty(nome)) throw new SituacaoComercialException(422, "NOME_OBRIGATORIO", "Dê um nome ao plano.");
        ValidarPreco(request.Preco, request.PrecoAnual, request.MotoboysBase);
        var modulos = Modulos(request.Modulos);
        await using var connection = await source.OpenConnectionAsync(ct);
        var baseChave = ChaveDe(nome);
        var chave = baseChave;
        for (var i = 2; await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM pacotes_comerciais WHERE chave=@C);", new { C = chave }, cancellationToken: ct))
                        || chave == "personalizado"; i++)
            chave = $"{baseChave}_{i}";
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pacotes_comerciais (chave, nome, modulos_ativos, pacotes_motoboy_base, motoboys_base, preco, preco_anual, cor, icone, frase, atualizado_por, ordem)
VALUES (@Chave, @Nome, @Modulos::modulo_enum[], 1, @MotoboysBase, @Preco, @PrecoAnual, @Cor, @Icone, @Frase, @UsuarioId, 50);",
            new { Chave = chave, Nome = nome, Modulos = modulos, request.MotoboysBase, request.Preco, request.PrecoAnual, request.Cor, request.Icone, request.Frase, UsuarioId = usuarioId },
            cancellationToken: ct));
        return ToDto(await connection.QuerySingleAsync<Row>(new CommandDefinition(SelectSql + " WHERE p.chave=@Chave;", new { Chave = chave }, cancellationToken: ct)));
    }

    public async Task<PacoteComercialDto> ArquivarAsync(string chave, bool arquivado, int usuarioId, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var linhas = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pacotes_comerciais SET arquivado=@A, atualizado_em=NOW(), atualizado_por=@U WHERE chave=@C;",
            new { A = arquivado, U = usuarioId, C = chave }, cancellationToken: ct));
        if (linhas == 0) throw new SituacaoComercialException(404, "PACOTE_NAO_ENCONTRADO", "Pacote não encontrado.");
        return ToDto(await connection.QuerySingleAsync<Row>(new CommandDefinition(SelectSql + " WHERE p.chave=@C;", new { C = chave }, cancellationToken: ct)));
    }

    public async Task ExcluirAsync(string chave, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var lojas = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM estabelecimentos WHERE plano_comercial=@C;", new { C = chave }, cancellationToken: ct));
        if (lojas > 0) throw new SituacaoComercialException(409, "PACOTE_EM_USO", $"{lojas} loja(s) usam este plano. Arquive em vez de excluir.");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM pacotes_comerciais WHERE chave=@C;", new { C = chave }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ProdutoAvulsoDto>> ListarAvulsosAsync(CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<AvulsoRow>(new CommandDefinition(AvulsoSql + " WHERE NOT arquivado ORDER BY preco, nome;", cancellationToken: ct))).Select(ToDto).ToArray();
    }

    public async Task<ProdutoAvulsoDto> SalvarAvulsoAsync(long? id, SalvarProdutoAvulsoRequest request, CancellationToken ct = default)
    {
        var nome = request.Nome?.Trim();
        if (string.IsNullOrEmpty(nome)) throw new SituacaoComercialException(422, "NOME_OBRIGATORIO", "Dê um nome ao avulso.");
        ValidarPreco(request.Preco, null, request.Motoboys);
        var modulos = request.Modulos is { Count: > 0 } ? Modulos(request.Modulos) : Array.Empty<string>();
        await using var connection = await source.OpenConnectionAsync(ct);
        var novoId = id.HasValue
            ? await connection.ExecuteScalarAsync<long?>(new CommandDefinition(@"
UPDATE produtos_avulsos SET nome=@Nome, preco=@Preco, modulos_ativos=@Modulos::modulo_enum[], motoboys=@Motoboys, cor=@Cor, atualizado_em=NOW()
 WHERE id=@Id RETURNING id;", new { Id = id, Nome = nome, request.Preco, Modulos = modulos, request.Motoboys, request.Cor }, cancellationToken: ct))
            : await connection.ExecuteScalarAsync<long?>(new CommandDefinition(@"
INSERT INTO produtos_avulsos (nome, preco, modulos_ativos, motoboys, cor) VALUES (@Nome, @Preco, @Modulos::modulo_enum[], @Motoboys, @Cor) RETURNING id;",
                new { Nome = nome, request.Preco, Modulos = modulos, request.Motoboys, request.Cor }, cancellationToken: ct));
        if (novoId == null) throw new SituacaoComercialException(404, "AVULSO_NAO_ENCONTRADO", "Avulso não encontrado.");
        return ToDto(await connection.QuerySingleAsync<AvulsoRow>(new CommandDefinition(AvulsoSql + " WHERE id=@Id;", new { Id = novoId }, cancellationToken: ct)));
    }

    public async Task ExcluirAvulsoAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("UPDATE produtos_avulsos SET arquivado=TRUE, atualizado_em=NOW() WHERE id=@Id;", new { Id = id }, cancellationToken: ct));
    }

    private static PacoteComercialDto ToDto(Row row) => new(row.Chave, row.Nome,
        EstabelecimentoModuleMapper.ToUiModules(string.Empty, row.ModulosAtivos),
        row.MotoboysBase, row.LojasAfetadas, row.Preco, row.PrecoAnual, row.Cor, row.Icone, row.Frase, row.Arquivado);

    private static ProdutoAvulsoDto ToDto(AvulsoRow row) => new(row.Id, row.Nome, row.Preco,
        EstabelecimentoModuleMapper.ToUiModules(string.Empty, row.ModulosAtivos), row.Motoboys, row.Cor, row.Arquivado);
}
