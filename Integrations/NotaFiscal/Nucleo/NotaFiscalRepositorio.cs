using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace APIBack.Integrations.NotaFiscal.Nucleo;

public sealed class ConexaoRow
{
    public Guid EstabelecimentoId { get; set; }
    public string Provedor { get; set; } = Provedores.Focus;
    public string Ambiente { get; set; } = Ambientes.Homologacao;
    public string Status { get; set; } = StatusConexao.NaoConectado;
    public string ModoConta { get; set; } = ModosConta.DaLoja;
    public string? EmpresaProvedorId { get; set; }
    public string? ChaveCifrada { get; set; }
    public string? Regime { get; set; }
    public string? InscricaoEstadual { get; set; }
    public bool ConfirmouCertificado { get; set; }
    public bool ConfirmouCsc { get; set; }
    public DateTime? UltimoTesteEm { get; set; }
    public string? Mensagem { get; set; }
}

public interface INotaFiscalRepositorio
{
    Task<ConexaoRow?> ObterConexaoAsync(Guid loja, CancellationToken ct);
    Task GravarConexaoAsync(ConexaoRow linha, int usuarioId, CancellationToken ct);
    Task RemoverChaveAsync(Guid loja, int usuarioId, CancellationToken ct);
    Task GravarResultadoTesteAsync(Guid loja, string status, string? mensagem, DateTime em, CancellationToken ct);
    Task<string?> ObterCnpjAsync(Guid loja, CancellationToken ct);

    Task<RegrasDto?> ObterRegrasAsync(Guid loja, CancellationToken ct);
    Task GravarRegrasAsync(Guid loja, RegrasDto regras, int usuarioId, CancellationToken ct);

    Task<IReadOnlyList<ProdutoFiscalDto>> ListarProdutosAsync(Guid loja, CancellationToken ct);
    /// <summary>Devolve false se o produto nao e da loja. <paramref name="substituir"/>: true = os nulos limpam; false = so entram os preenchidos.</summary>
    Task<bool> GravarProdutoAsync(Guid loja, Guid produtoId, ProdutoFiscalRequest valores, bool substituir, CancellationToken ct);
    Task<int> GravarLoteAsync(Guid loja, IReadOnlyList<Guid> produtoIds, ProdutoFiscalRequest valores, CancellationToken ct);
}

public sealed class NotaFiscalRepositorio : INotaFiscalRepositorio
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _db;

    public NotaFiscalRepositorio(NpgsqlDataSource db) => _db = db;

    public async Task<ConexaoRow?> ObterConexaoAsync(Guid loja, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        return await cx.QueryFirstOrDefaultAsync<ConexaoRow>(new CommandDefinition(@"
SELECT estabelecimento_id AS EstabelecimentoId, provedor AS Provedor, ambiente AS Ambiente, status AS Status,
       modo_conta AS ModoConta, empresa_provedor_id AS EmpresaProvedorId, chave_cifrada AS ChaveCifrada,
       regime AS Regime, inscricao_estadual AS InscricaoEstadual, confirmou_certificado AS ConfirmouCertificado,
       confirmou_csc AS ConfirmouCsc, ultimo_teste_em AS UltimoTesteEm, mensagem AS Mensagem
  FROM nf_conexao WHERE estabelecimento_id = @Loja", new { Loja = loja }, cancellationToken: ct));
    }

    public async Task GravarConexaoAsync(ConexaoRow l, int usuarioId, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO nf_conexao (estabelecimento_id, provedor, ambiente, status, modo_conta, empresa_provedor_id, chave_cifrada, regime,
                        inscricao_estadual, confirmou_certificado, confirmou_csc, ultimo_teste_em, mensagem, atualizado_por, atualizado_em)
VALUES (@EstabelecimentoId, @Provedor, @Ambiente, @Status, @ModoConta, @EmpresaProvedorId, @ChaveCifrada, @Regime,
        @InscricaoEstadual, @ConfirmouCertificado, @ConfirmouCsc, @UltimoTesteEm, @Mensagem, @Usuario, NOW())
ON CONFLICT (estabelecimento_id) DO UPDATE SET
    provedor = EXCLUDED.provedor, ambiente = EXCLUDED.ambiente, status = EXCLUDED.status, modo_conta = EXCLUDED.modo_conta,
    empresa_provedor_id = EXCLUDED.empresa_provedor_id, chave_cifrada = EXCLUDED.chave_cifrada, regime = EXCLUDED.regime,
    inscricao_estadual = EXCLUDED.inscricao_estadual, confirmou_certificado = EXCLUDED.confirmou_certificado,
    confirmou_csc = EXCLUDED.confirmou_csc, ultimo_teste_em = EXCLUDED.ultimo_teste_em, mensagem = EXCLUDED.mensagem,
    atualizado_por = EXCLUDED.atualizado_por, atualizado_em = NOW()",
            new
            {
                l.EstabelecimentoId, l.Provedor, l.Ambiente, l.Status, l.ModoConta, l.EmpresaProvedorId, l.ChaveCifrada, l.Regime,
                l.InscricaoEstadual, l.ConfirmouCertificado, l.ConfirmouCsc, l.UltimoTesteEm, l.Mensagem, Usuario = usuarioId,
            }, cancellationToken: ct));
    }

    public async Task RemoverChaveAsync(Guid loja, int usuarioId, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE nf_conexao SET chave_cifrada = NULL, status = 'nao_conectado', ultimo_teste_em = NULL, mensagem = NULL,
       atualizado_por = @Usuario, atualizado_em = NOW()
 WHERE estabelecimento_id = @Loja", new { Loja = loja, Usuario = usuarioId }, cancellationToken: ct));
    }

    public async Task GravarResultadoTesteAsync(Guid loja, string status, string? mensagem, DateTime em, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE nf_conexao SET status = @Status, mensagem = @Mensagem, ultimo_teste_em = @Em, atualizado_em = NOW()
 WHERE estabelecimento_id = @Loja", new { Loja = loja, Status = status, Mensagem = mensagem, Em = em }, cancellationToken: ct));
    }

    public async Task<string?> ObterCnpjAsync(Guid loja, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        return await cx.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT cnpj_loja FROM estabelecimentos WHERE id = @Loja", new { Loja = loja }, cancellationToken: ct));
    }

    // ---------- Regras ----------

    public async Task<RegrasDto?> ObterRegrasAsync(Guid loja, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        var row = await cx.QueryFirstOrDefaultAsync<RegrasRow>(new CommandDefinition(@"
SELECT modo AS Modo, canais::text AS Canais, formas_pagamento::text AS FormasPagamento, espera_minutos AS EsperaMinutos
  FROM nf_regras WHERE estabelecimento_id = @Loja", new { Loja = loja }, cancellationToken: ct));
        if (row == null) return null;
        return new RegrasDto
        {
            Modo = row.Modo,
            EsperaMinutos = row.EsperaMinutos,
            Canais = Ler<CanaisDto>(row.Canais),
            FormasPagamento = Ler<FormasPagamentoDto>(row.FormasPagamento),
        };
    }

    public async Task GravarRegrasAsync(Guid loja, RegrasDto r, int usuarioId, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO nf_regras (estabelecimento_id, modo, canais, formas_pagamento, espera_minutos, atualizado_por, atualizado_em)
VALUES (@Loja, @Modo, CAST(@Canais AS jsonb), CAST(@Formas AS jsonb), @Espera, @Usuario, NOW())
ON CONFLICT (estabelecimento_id) DO UPDATE SET
    modo = EXCLUDED.modo, canais = EXCLUDED.canais, formas_pagamento = EXCLUDED.formas_pagamento,
    espera_minutos = EXCLUDED.espera_minutos, atualizado_por = EXCLUDED.atualizado_por, atualizado_em = NOW()",
            new
            {
                Loja = loja, r.Modo, Canais = JsonSerializer.Serialize(r.Canais, Json),
                Formas = JsonSerializer.Serialize(r.FormasPagamento, Json), Espera = r.EsperaMinutos, Usuario = usuarioId,
            }, cancellationToken: ct));
    }

    private static T Ler<T>(string? json) where T : new() =>
        string.IsNullOrWhiteSpace(json) ? new T() : JsonSerializer.Deserialize<T>(json, Json) ?? new T();

    // ---------- Produtos ----------

    public async Task<IReadOnlyList<ProdutoFiscalDto>> ListarProdutosAsync(Guid loja, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        var linhas = await cx.QueryAsync<ProdutoRow>(new CommandDefinition(@"
SELECT p.id AS ProdutoId, p.nome AS Nome, p.imagem_url AS Foto, c.nome AS Categoria,
       f.ncm AS Ncm, f.cfop AS Cfop, f.csosn AS Csosn, f.cst AS Cst, f.origem::int AS Origem, f.unidade AS Unidade
  FROM cardapio_produto p
  JOIN cardapio_categoria c ON c.id = p.categoria_id AND c.deleted_at IS NULL
  LEFT JOIN nf_produto_fiscal f ON f.produto_id = p.id AND f.estabelecimento_id = @Loja
 WHERE p.id_estabelecimento = @Loja AND p.deleted_at IS NULL
 ORDER BY c.nome, p.nome", new { Loja = loja }, cancellationToken: ct));
        return linhas.Select(x => new ProdutoFiscalDto
        {
            ProdutoId = x.ProdutoId, Nome = x.Nome, Foto = x.Foto, Categoria = x.Categoria,
            Ncm = x.Ncm, Cfop = x.Cfop, Csosn = x.Csosn, Cst = x.Cst, Origem = x.Origem, Unidade = x.Unidade,
            Completo = NotaFiscalRegras.ProdutoCompleto(x.Ncm, x.Cfop, x.Csosn, x.Cst),
        }).ToList();
    }

    public async Task<bool> GravarProdutoAsync(Guid loja, Guid produtoId, ProdutoFiscalRequest v, bool substituir, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        return await GravarProdutoAsync(cx, null, loja, produtoId, v, substituir, ct) > 0;
    }

    public async Task<int> GravarLoteAsync(Guid loja, IReadOnlyList<Guid> produtoIds, ProdutoFiscalRequest v, CancellationToken ct)
    {
        await using var cx = await _db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        var total = 0;
        foreach (var id in produtoIds.Distinct())
            total += await GravarProdutoAsync(cx, tx, loja, id, v, false, ct);
        await tx.CommitAsync(ct);
        return total;
    }

    // O INSERT ... SELECT so insere produto da propria loja; o WHERE do conflito impede tocar em linha de outra loja.
    private static Task<int> GravarProdutoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, Guid produtoId,
        ProdutoFiscalRequest v, bool substituir, CancellationToken ct) => cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO nf_produto_fiscal (produto_id, estabelecimento_id, ncm, cfop, csosn, cst, origem, unidade, atualizado_em)
SELECT p.id, p.id_estabelecimento, CAST(@Ncm AS text), CAST(@Cfop AS text), CAST(@Csosn AS text), CAST(@Cst AS text),
       CAST(@Origem AS smallint), CAST(@Unidade AS text), NOW()
  FROM cardapio_produto p
 WHERE p.id = @Produto AND p.id_estabelecimento = @Loja AND p.deleted_at IS NULL
ON CONFLICT (produto_id) DO UPDATE SET
    ncm = CASE WHEN @Substituir THEN EXCLUDED.ncm ELSE COALESCE(EXCLUDED.ncm, nf_produto_fiscal.ncm) END,
    cfop = CASE WHEN @Substituir THEN EXCLUDED.cfop ELSE COALESCE(EXCLUDED.cfop, nf_produto_fiscal.cfop) END,
    csosn = CASE WHEN @Substituir THEN EXCLUDED.csosn ELSE COALESCE(EXCLUDED.csosn, nf_produto_fiscal.csosn) END,
    cst = CASE WHEN @Substituir THEN EXCLUDED.cst ELSE COALESCE(EXCLUDED.cst, nf_produto_fiscal.cst) END,
    origem = CASE WHEN @Substituir THEN EXCLUDED.origem ELSE COALESCE(EXCLUDED.origem, nf_produto_fiscal.origem) END,
    unidade = CASE WHEN @Substituir THEN EXCLUDED.unidade ELSE COALESCE(EXCLUDED.unidade, nf_produto_fiscal.unidade) END,
    atualizado_em = NOW()
 WHERE nf_produto_fiscal.estabelecimento_id = EXCLUDED.estabelecimento_id",
        new { Loja = loja, Produto = produtoId, v.Ncm, v.Cfop, v.Csosn, v.Cst, v.Origem, v.Unidade, Substituir = substituir }, tx, cancellationToken: ct));

    private sealed class RegrasRow
    {
        public string Modo { get; set; } = ModosEmissao.Manual;
        public string? Canais { get; set; }
        public string? FormasPagamento { get; set; }
        public int EsperaMinutos { get; set; }
    }

    private sealed class ProdutoRow
    {
        public Guid ProdutoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Foto { get; set; }
        public string? Categoria { get; set; }
        public string? Ncm { get; set; }
        public string? Cfop { get; set; }
        public string? Csosn { get; set; }
        public string? Cst { get; set; }
        public int? Origem { get; set; }
        public string? Unidade { get; set; }
    }
}
