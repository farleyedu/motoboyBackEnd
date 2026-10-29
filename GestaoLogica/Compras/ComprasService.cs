using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using APIBack.GestaoLogica.Vinculos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Compras;

public static class FormaCompra
{
    public const string APagar = "a_pagar", Pago = "pago", Cartao = "cartao";
}

/// <summary>Como a compra foi paga. <c>a_pagar</c> (boleto/fiado, parcelável), <c>pago</c> (já saiu do banco/caixa), <c>cartao</c> (fatura).</summary>
public sealed record RegistrarCompraRequest(Guid DocumentoId, string Forma, Guid? ContaId, int Parcelas = 1, DateOnly? Vencimento = null,
    DateOnly? DataPagamento = null, string? Categoria = null, string? Finalidade = null);

/// <summary>
/// Conferência de uma linha. <c>QuantidadeRecebida</c> na unidade do cupom (padrão: a do cupom). Fora do estoque, <c>Destino</c>
/// diz por quê: <c>consumo</c> (gasto na hora, como o desengordurante) ou <c>nao_controlar</c> (a loja não controla esse item).
/// <c>ConsumoDireto = true</c> sem destino continua valendo como <c>consumo</c>. Nenhum dos dois gera movimento.
/// </summary>
public sealed record ConferirItemRequest(decimal? QuantidadeRecebida, bool ConsumoDireto, bool ConfirmacaoHumana, string? Destino = null);

public static class DestinoLinha
{
    public const string Estoque = "estoque", Consumo = "consumo", NaoControlar = "nao_controlar";
}

public static class StatusLinha
{
    public const string SemVinculo = "sem_vinculo", Pendente = "pendente", Conferido = "conferido", ConsumoDireto = "consumo_direto",
        NaoControlar = "nao_controlar";
}

/// <summary><c>ConfirmadoPor</c>/<c>ConfirmadoPorNome</c>/<c>ConfirmadoEm</c>: quem conferiu a linha e quando (null se pendente).</summary>
public sealed record LinhaCompraDto(int Linha, string Descricao, decimal Quantidade, string Unidade, decimal ValorTotal, string Status,
    Guid? VinculoId, Guid? InsumoId, string? InsumoNome, decimal? Fator, string? UnidadeBase, decimal? QuantidadeEsperada,
    decimal? QuantidadeEntrada, Guid? ConferenciaId, Guid? MovimentoId, int? ConfirmadoPor = null, string? ConfirmadoPorNome = null,
    DateTimeOffset? ConfirmadoEm = null);

public sealed record CompraDto(Guid DocumentoId, Guid AcontecimentoId, FornecedorLido Fornecedor, DateTimeOffset DataFato, decimal Total,
    bool Estornada, int Conferidas, int Pendentes, IReadOnlyList<LinhaCompraDto> Linhas, AcontecimentoDto Acontecimento);

/// <summary>
/// Compra a partir do documento lido. A compra entra no financeiro na hora (regra 4: existe sem a conferência); o estoque só
/// recebe entrada quando uma pessoa confere a linha. Correção de quantidade conferida = ajuste do movimento no livro.
/// </summary>
public interface IComprasService
{
    Task<CompraDto> Registrar(Guid loja, int usuario, RegistrarCompraRequest req, CancellationToken ct);
    Task<CompraDto?> Obter(Guid loja, Guid documentoId, CancellationToken ct);
    Task<CompraDto> Conferir(Guid loja, int usuario, Guid documentoId, int linha, ConferirItemRequest req, CancellationToken ct);
}

public sealed class ComprasService(NpgsqlDataSource dataSource, IAcontecimentosService livro, TimeProvider? relogio = null) : IComprasService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    public const string TipoCompra = "compra", TipoConferencia = "conferencia_item";

    private sealed record Documento(Guid Id, DocumentoLido Lido, DateTimeOffset CriadoEm);
    private sealed record Conferencia(string OrigemId, Guid Id, string Dados, Guid? MovimentoId, decimal? Quantidade, int? ResponsavelId,
        string? ResponsavelNome, DateTimeOffset RegistradoEm);
    private sealed record VinculoAtivo(Guid Id, Guid InsumoId, string? InsumoNome, decimal Fator, string UnidadeBase);

    public async Task<CompraDto> Registrar(Guid loja, int usuario, RegistrarCompraRequest req, CancellationToken ct)
    {
        Documento doc;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
            doc = await ObterDocumento(c, loja, req.DocumentoId);
        if (doc.Lido.Total <= 0) throw Invalido("O documento não tem total: confira a leitura antes de lançar a compra.");
        var lanc = new LancamentoRequest
        {
            Natureza = Natureza.Despesa, Valor = doc.Lido.Total, Categoria = string.IsNullOrWhiteSpace(req.Categoria) ? "insumos" : req.Categoria.Trim(),
            Finalidade = string.IsNullOrWhiteSpace(req.Finalidade) ? null : req.Finalidade.Trim(),
            Descricao = $"Compra {doc.Lido.Fornecedor.Nome ?? "sem fornecedor"}", Parcelas = Math.Max(1, req.Parcelas),
        };
        switch (req.Forma)
        {
            case FormaCompra.APagar:
                lanc.DataVencimento = req.Vencimento;
                break;
            case FormaCompra.Pago:
                if (req.ContaId is null) throw Invalido("Diga de qual conta o dinheiro saiu.");
                if (lanc.Parcelas > 1) throw Invalido("Compra já paga não tem parcelas.");
                lanc.ContaId = req.ContaId;
                lanc.DataPagamento = req.DataPagamento ?? DateOnly.FromDateTime((doc.Lido.DataEmissao ?? doc.CriadoEm).Date);
                break;
            case FormaCompra.Cartao:
                lanc.ContaId = req.ContaId ?? throw Invalido("Escolha o cartão da empresa.");
                break;
            default:
                throw Invalido("Forma de pagamento deve ser a_pagar, pago ou cartao.");
        }
        var dataFato = Fato(doc);
        await livro.Publicar(loja, usuario, new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = $"compras:compra:{doc.Id}", Tipo = TipoCompra, Modulo = ModuloGestao.Compras, DataFato = dataFato,
            Descricao = Cortar($"{doc.Lido.Fornecedor.Nome ?? "Compra"} — {(doc.Lido.Numero is { } n ? "documento " + n : "sem número")}", 300),
            Origem = new OrigemRef { Tipo = "compra", Id = doc.Id.ToString() },
            DocumentoChave = DocumentoRegras.ChaveLivro(doc.Lido),
            Dados = JsonSerializer.SerializeToElement(new { documentoId = doc.Id, forma = req.Forma, itens = doc.Lido.Itens.Count }, Json),
            Lancamentos = new() { lanc },
        }, ct);
        return (await Obter(loja, doc.Id, ct))!;
    }

    public async Task<CompraDto?> Obter(Guid loja, Guid documentoId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var doc = await ObterDocumento(c, loja, documentoId);
        var compraId = await c.ExecuteScalarAsync<Guid?>(@"
SELECT id FROM gestao_acontecimentos WHERE estabelecimento_id = @loja AND tipo = 'compra' AND origem_tipo = 'compra'
   AND origem_id = @origem AND corrige_id IS NULL", new { loja, origem = documentoId.ToString() });
        if (compraId is not { } id) return null;
        var acontecimento = await livro.Obter(loja, id, ct) ?? throw new InvalidOperationException("Compra sumiu do livro.");
        var estornada = await c.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM gestao_acontecimentos WHERE estabelecimento_id = @loja AND corrige_id = @id AND correcao = 'estorno')",
            new { loja, id });

        // Conferências da compra, com a quantidade líquida (entrada + ajustes/estorno) do movimento.
        var conferencias = (await c.QueryAsync<Conferencia>(@"
SELECT a.origem_id AS OrigemId, a.id, a.dados::text AS Dados, m.id AS MovimentoId,
       m.quantidade + coalesce((SELECT sum(x.quantidade) FROM gestao_movimentos_estoque x
                                 WHERE x.estabelecimento_id = m.estabelecimento_id AND x.corrige_movimento_id = m.id), 0) AS Quantidade,
       a.responsavel_id AS ResponsavelId, (SELECT u.nome FROM usuario u WHERE u.id = a.responsavel_id) AS ResponsavelNome,
       a.registrado_em AS RegistradoEm
  FROM gestao_acontecimentos a
  LEFT JOIN gestao_movimentos_estoque m ON m.estabelecimento_id = a.estabelecimento_id AND m.acontecimento_id = a.id AND m.linha = 1
 WHERE a.estabelecimento_id = @loja AND a.tipo = 'conferencia_item' AND a.origem_tipo = 'compra_item' AND a.origem_id LIKE @prefixo
   AND a.corrige_id IS NULL", new { loja, prefixo = documentoId + ":%" })).ToDictionary(x => int.Parse(x.OrigemId.Split(':')[1]));
        var linhas = new List<LinhaCompraDto>();
        foreach (var item in doc.Lido.Itens)
        {
            var v = await VinculoDaLinha(c, loja, doc.Lido.Fornecedor.Cnpj, item);
            var esperado = v is null ? (decimal?)null : Math.Round(item.Quantidade * v.Fator, 4);
            if (conferencias.TryGetValue(item.Linha, out var conf))
            {
                linhas.Add(new LinhaCompraDto(item.Linha, item.Descricao, item.Quantidade, item.Unidade, item.ValorTotal,
                    StatusDaConferencia(conf.Dados), v?.Id, v?.InsumoId, v?.InsumoNome, v?.Fator,
                    v?.UnidadeBase, esperado, conf.Quantidade, conf.Id, conf.MovimentoId, conf.ResponsavelId, conf.ResponsavelNome,
                    conf.RegistradoEm));
            }
            else
                linhas.Add(new LinhaCompraDto(item.Linha, item.Descricao, item.Quantidade, item.Unidade, item.ValorTotal,
                    v is null ? StatusLinha.SemVinculo : StatusLinha.Pendente, v?.Id, v?.InsumoId, v?.InsumoNome, v?.Fator,
                    v?.UnidadeBase, esperado, null, null, null));
        }
        var conferidas = linhas.Count(l => l.Status is StatusLinha.Conferido or StatusLinha.ConsumoDireto or StatusLinha.NaoControlar);
        return new CompraDto(doc.Id, id, doc.Lido.Fornecedor, acontecimento.DataFato, doc.Lido.Total, estornada,
            conferidas, linhas.Count - conferidas, linhas, acontecimento);
    }

    public async Task<CompraDto> Conferir(Guid loja, int usuario, Guid documentoId, int linha, ConferirItemRequest req, CancellationToken ct)
    {
        if (!req.ConfirmacaoHumana) throw new GestaoException("CONFIRMACAO_HUMANA", "Confirme que você conferiu o item.", 422);
        var compra = await Obter(loja, documentoId, ct)
            ?? throw Invalido("Lance a compra antes de conferir os itens.");
        if (compra.Estornada) throw new GestaoException("JA_ESTORNADO", "Esta compra foi estornada.", 409);
        var alvo = compra.Linhas.FirstOrDefault(l => l.Linha == linha)
            ?? throw new GestaoException("NAO_ENCONTRADO", $"O documento não tem a linha {linha}.", 404);
        if (alvo.ConferenciaId is not null)
            throw new GestaoException("DUPLICADO", "Esta linha já foi conferida. Para corrigir a quantidade, faça um ajuste do movimento.", 409);

        var destino = req.Destino?.Trim() is { Length: > 0 } d ? d : req.ConsumoDireto ? DestinoLinha.Consumo : DestinoLinha.Estoque;
        if (destino is not (DestinoLinha.Estoque or DestinoLinha.Consumo or DestinoLinha.NaoControlar))
            throw Invalido("Destino deve ser estoque, consumo ou nao_controlar.");
        var foraDoEstoque = destino != DestinoLinha.Estoque;
        var recebida = Math.Round(req.QuantidadeRecebida ?? alvo.Quantidade, 4);
        var movimentos = new List<MovimentoRequest>();
        if (!foraDoEstoque)
        {
            if (alvo.VinculoId is null || alvo.InsumoId is null || alvo.Fator is null)
                throw Invalido("Vincule a linha a um insumo antes de conferir (ou marque como consumo direto).");
            if (recebida <= 0) throw Invalido("Quantidade recebida precisa ser maior que zero. Se nada chegou, deixe a linha pendente.");
            movimentos.Add(new MovimentoRequest
            {
                ItemId = alvo.InsumoId.Value, ItemNome = Cortar(alvo.InsumoNome ?? alvo.Descricao, 120), Tipo = TipoMovimento.Entrada,
                Quantidade = Math.Round(recebida * alvo.Fator.Value, 4), Unidade = alvo.UnidadeBase,
            });
        }
        var agora = _relogio.GetUtcNow();
        await livro.Publicar(loja, usuario, new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = $"compras:conferencia:{documentoId}:{linha}", Tipo = TipoConferencia, Modulo = ModuloGestao.Compras,
            DataFato = agora, Descricao = Cortar(destino switch
            {
                DestinoLinha.Consumo => "Consumo direto: ", DestinoLinha.NaoControlar => "Sem controle de estoque: ", _ => "Conferência: ",
            } + alvo.Descricao, 300),
            Origem = new OrigemRef { Tipo = "compra_item", Id = $"{documentoId}:{linha}" },
            Dados = JsonSerializer.SerializeToElement(new
            {
                documentoId, compraId = compra.AcontecimentoId, linha, quantidadeCupom = alvo.Quantidade, quantidadeRecebida = recebida,
                unidadeCupom = alvo.Unidade, fator = alvo.Fator, vinculoId = alvo.VinculoId, consumoDireto = destino == DestinoLinha.Consumo,
                destino,
            }, Json),
            Movimentos = movimentos, ConfirmacaoHumana = true,
        }, ct);

        // O preço desta compra entra no histórico do insumo (base da divergência), uma vez por vínculo e linha.
        if (alvo.VinculoId is { } vinculoId && alvo.QuantidadeEsperada is > 0)
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            await c.ExecuteAsync(@"
INSERT INTO gestao_vinculo_precos (id, estabelecimento_id, vinculo_id, insumo_id, documento_id, linha, quantidade_base, valor_total,
    preco_base, observado_em, criado_por, criado_em)
VALUES (@id, @loja, @vinculoId, @insumoId, @documentoId, @linha, @quantidade, @valor, @preco, @observado, @usuario, @agora)
ON CONFLICT (vinculo_id, documento_id, linha) WHERE documento_id IS NOT NULL DO NOTHING", new
            {
                id = Guid.NewGuid(), loja, vinculoId, insumoId = alvo.InsumoId, documentoId, linha, quantidade = alvo.QuantidadeEsperada,
                valor = alvo.ValorTotal, preco = VinculoRegras.PrecoBase(alvo.ValorTotal, alvo.QuantidadeEsperada.Value),
                observado = compra.DataFato.ToUniversalTime(), usuario, agora,
            });
        }
        return (await Obter(loja, documentoId, ct))!;
    }

    /// <summary>Status de uma linha conferida. Conferências antigas só têm <c>consumoDireto</c>.</summary>
    public static string StatusDaConferencia(string dados)
    {
        var raiz = JsonDocument.Parse(dados).RootElement;
        if (raiz.TryGetProperty("destino", out var d) && d.ValueKind == JsonValueKind.String)
            return d.GetString() switch
            {
                DestinoLinha.Consumo => StatusLinha.ConsumoDireto, DestinoLinha.NaoControlar => StatusLinha.NaoControlar, _ => StatusLinha.Conferido,
            };
        return raiz.TryGetProperty("consumoDireto", out var cd) && cd.ValueKind == JsonValueKind.True ? StatusLinha.ConsumoDireto : StatusLinha.Conferido;
    }

    /// <summary>Vínculo ativo da linha: o do fornecedor vence o de "qualquer fornecedor"; a embalagem precisa ser a mesma.</summary>
    private static async Task<VinculoAtivo?> VinculoDaLinha(NpgsqlConnection c, Guid loja, string? cnpj, ItemLido item)
    {
        var temInsumos = await c.ExecuteScalarAsync<bool>("SELECT to_regclass('insumo') IS NOT NULL");
        var nome = temInsumos ? "(SELECT i.nome FROM insumo i WHERE i.estabelecimento_id = v.estabelecimento_id AND i.id = v.insumo_id)" : "NULL";
        return await c.QueryFirstOrDefaultAsync<VinculoAtivo>($@"
SELECT v.id, v.insumo_id AS InsumoId, {nome} AS InsumoNome, v.fator, v.unidade_base AS UnidadeBase FROM gestao_vinculos v
 WHERE v.estabelecimento_id = @loja AND v.estado = 'confirmado' AND v.nome_chave = @chave AND v.unidade_cupom = @unidade
   AND v.fornecedor_cnpj IN (@cnpj, '')
 ORDER BY (v.fornecedor_cnpj = '') LIMIT 1", new
        {
            loja, cnpj = cnpj ?? "", chave = Cortar(DocumentoRegras.NomeChave(item.Descricao), 160),
            unidade = Cortar(item.Unidade.Trim().ToUpperInvariant(), 10),
        });
    }

    private static async Task<Documento> ObterDocumento(NpgsqlConnection c, Guid loja, Guid id)
    {
        var row = await c.QuerySingleOrDefaultAsync<(Guid Id, string Leitura, DateTime CriadoEm)>(
            "SELECT id, leitura::text, criado_em FROM gestao_documentos WHERE estabelecimento_id = @loja AND id = @id", new { loja, id });
        if (row.Id == Guid.Empty) throw new GestaoException("NAO_ENCONTRADO", "Documento não encontrado.", 404);
        return new Documento(row.Id, JsonSerializer.Deserialize<DocumentoLido>(row.Leitura, Json)!,
            new DateTimeOffset(DateTime.SpecifyKind(row.CriadoEm, DateTimeKind.Utc)));
    }

    /// <summary>Data do fato = emissão do documento; sem ela (ou no futuro, relógio do emissor adiantado), a hora do envio.</summary>
    private DateTimeOffset Fato(Documento d) => d.Lido.DataEmissao is { } e && e <= _relogio.GetUtcNow() ? e : d.CriadoEm;

    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
