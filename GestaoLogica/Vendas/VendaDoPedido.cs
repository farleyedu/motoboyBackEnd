using System.Globalization;
using System.Text;
using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.DTOs.Delivery;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Vendas;

// 6C1-A: a conclusao do pedido publica a venda no livro, na mesma transacao. docs/zippy-gestao/6c1-a-relatorio.md.

public static class ModalidadeVenda
{
    public const string Salao = "salao", Retirada = "retirada", Delivery = "delivery";
}

/// <summary>Parte do pagamento. <c>Forma</c> null = forma desconhecida (fica a receber, sem conta).</summary>
public sealed record ParcelaVenda(string? Forma, decimal Valor, bool FormaConfirmada);

/// <summary>Regras puras da venda: forma de pagamento e composicao.</summary>
public static class VendaRegras
{
    public const string Din = "din", Pix = "pix", Deb = "deb", Cre = "cre", Ifood = "ifood";

    /// <summary>
    /// Texto gravado no pedido (ou metodo do app) para a forma do livro. Cartao sem dizer se e debito ou credito vira "cre" nao
    /// confirmado (os dois caem na maquininha); o resto que nao se reconhece (link, outro, legado) fica null.
    /// </summary>
    public static (string? Forma, bool Confirmada) Forma(string? texto)
    {
        var t = Chave(texto);
        return t switch
        {
            "" => (null, false),
            "dinheiro" or "din" or "cash" => (Din, true),
            "pix" or "pix_online" or "pago_online_-_pix" or "pago_online_pix" => (Pix, true),
            "debito" or "deb" or "cartao_de_debito" => (Deb, true),
            "credito" or "cre" or "cartao_de_credito" or "cartao_online" or "credito_online" or "pago_online_-_cartao"
                or "pago_online_cartao" => (Cre, true),
            "cartao_entrega" or "cartao" or "cartao_na_entrega" => (Cre, false),
            _ => (null, false),
        };
    }

    /// <summary>
    /// Partes da venda. iFood: o pre-pago e recebivel iFood e o cobrado na entrega segue a forma conferida. Pagamentos conferidos
    /// (app do motoboy) valem sobre o texto do pedido. Mesma forma soma; nada se perde nem duplica.
    /// </summary>
    public static IReadOnlyList<ParcelaVenda> Composicao(decimal total, string? tipoPagamento,
        IReadOnlyList<DeliveryPaymentPart>? conferidos, decimal? prepagoIfood)
    {
        var partes = new List<ParcelaVenda>();
        if (prepagoIfood is > 0) partes.Add(new ParcelaVenda(Ifood, prepagoIfood.Value, true));
        if (conferidos is { Count: > 0 })
            partes.AddRange(conferidos.Where(p => p.Amount > 0).Select(p =>
            {
                var (forma, ok) = Forma(p.Method);
                return new ParcelaVenda(forma, p.Amount, ok);
            }));
        else
        {
            var resto = total - (prepagoIfood ?? 0);
            if (resto > 0)
            {
                var (forma, ok) = prepagoIfood.HasValue ? ((string?)null, false) : Forma(tipoPagamento);
                partes.Add(new ParcelaVenda(forma, resto, ok));
            }
        }
        return partes.GroupBy(p => (p.Forma, p.FormaConfirmada))
            .Select(g => new ParcelaVenda(g.Key.Forma, g.Sum(p => p.Valor), g.Key.FormaConfirmada)).ToList();
    }

    private static string Chave(string? texto)
    {
        var t = (texto ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch == ' ' ? '_' : ch);
        return sb.ToString();
    }
}

/// <summary>Ponto unico onde a conclusao do pedido vira venda (e o cancelamento autorizado vira estorno) no livro.</summary>
public static class VendaDoPedido
{
    public static string Chave(int pedidoId) => $"venda:pedido:{pedidoId}";

    /// <summary>
    /// Publica a venda do pedido dentro da transacao da conclusao. Sem o livro no banco, pedido do simulador ou venda ja publicada:
    /// nao grava nada. Erro do livro desfaz a conclusao (vira <see cref="DeliveryDomainException"/>).
    /// </summary>
    public static async Task<Guid?> PublicarAsync(IAcontecimentosService livro, NpgsqlConnection c, NpgsqlTransaction tx, Guid loja,
        int pedidoId, int? responsavelId, string modalidade, IReadOnlyList<DeliveryPaymentPart>? conferidos = null,
        CancellationToken ct = default)
    {
        if (!await Existe(c, tx, "gestao_acontecimentos")) return null;
        var pedido = await c.QuerySingleOrDefaultAsync<PedidoVenda>(@"
SELECT p.id AS Id,
       CASE WHEN replace(btrim(p.value::text), ',', '.') ~ '^[0-9]+(\.[0-9]+)?$' THEN replace(btrim(p.value::text), ',', '.')::numeric END AS Total,
       p.tipo_pagamento::text AS TipoPagamento,
       to_jsonb(p)->>'origem' AS Origem, (now() AT TIME ZONE 'UTC') AS Agora
  FROM pedido p WHERE p.id = @pedidoId AND p.id_estabelecimento = @loja;", new { pedidoId, loja }, tx)
            ?? throw new DeliveryDomainException(404, "PEDIDO_NOT_FOUND", "Pedido nao encontrado neste estabelecimento.");
        if (pedido.Origem == APIBack.Model.Delivery.PedidoOrigem.Simulador) return null;

        var chave = Chave(pedidoId);
        await AcontecimentoSql.TravarChave(c, tx, loja, chave);
        var existente = await VendaAtual(c, tx, loja, pedidoId);
        if (existente != null) return existente.Id;

        var ifood = await Existe(c, tx, "ifood_order_links") ? await c.QuerySingleOrDefaultAsync<IfoodVenda>(@"
SELECT s.order_amount AS Total, s.prepaid_amount AS Prepago, s.order_type AS Tipo
  FROM ifood_order_links l JOIN ifood_order_snapshots s USING (merchant_id, external_order_id, estabelecimento_id)
 WHERE l.estabelecimento_id = @loja AND l.pedido_id = @pedidoId;", new { loja, pedidoId }, tx) : null;
        var total = ifood?.Total ?? pedido.Total ?? conferidos?.Sum(p => p.Amount) ?? 0m;
        var partes = VendaRegras.Composicao(total, pedido.TipoPagamento, conferidos, ifood == null ? null : ifood.Prepago ?? 0m);

        var agora = new DateTimeOffset(DateTime.SpecifyKind(pedido.Agora, DateTimeKind.Utc));
        var fuso = HorarioRegras.ResolverFuso(await AcontecimentoSql.Fuso(c, tx, loja));
        var hoje = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(agora, fuso).DateTime);
        var contas = await Contas(c, tx, loja, hoje);
        var pendencias = new List<string>();
        if (total <= 0) pendencias.Add("valor_desconhecido");
        var lancamentos = new List<LancamentoRequest>();
        var pagamentos = new List<object>();
        foreach (var p in partes)
        {
            var (conta, recebido, pendencia) = p.Forma switch
            {
                VendaRegras.Din when contas.Gaveta is { } g && contas.CaixaAberto => (g, true, (string?)null),
                VendaRegras.Din => ((Guid?)null, false, contas.Gaveta == null ? "sem_conta_caixa" : "caixa_fechado"),
                VendaRegras.Pix => (contas.Maquininha, contas.Maquininha.HasValue, contas.Maquininha == null ? "sem_conta_maquininha" : null),
                VendaRegras.Deb or VendaRegras.Cre => (contas.Maquininha, false, contas.Maquininha == null ? "sem_conta_maquininha" : null),
                VendaRegras.Ifood => (contas.Ifood, false, contas.Ifood == null ? "sem_conta_ifood" : null),
                _ => ((Guid?)null, false, "forma_desconhecida"),
            };
            if (pendencia != null && !pendencias.Contains(pendencia)) pendencias.Add(pendencia);
            lancamentos.Add(new LancamentoRequest
            {
                Natureza = Natureza.Receita, Valor = p.Valor, ContaId = conta, DataPagamento = recebido ? hoje : null,
                Descricao = $"Pagamento: {p.Forma ?? "a conferir"}",
            });
            pagamentos.Add(new { forma = p.Forma, valor = p.Valor, situacao = recebido ? StatusLancamento.Recebido : StatusLancamento.AReceber,
                formaConfirmada = p.FormaConfirmada });
        }

        var principal = partes.Where(p => p.Forma != null).OrderByDescending(p => p.Valor).FirstOrDefault()?.Forma;
        var dados = new Dictionary<string, object?>
        {
            ["modalidade"] = modalidade, ["origemPedido"] = pedido.Origem, ["total"] = total, ["pagamentos"] = pagamentos,
        };
        if (principal != null) dados["forma"] = principal;
        if (ifood != null) dados["ifoodTipo"] = ifood.Tipo;
        if (pendencias.Count > 0) dados["pendencias"] = pendencias;

        try
        {
            var r = await livro.Publicar(c, tx, loja, responsavelId, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = chave, Tipo = "venda", Modulo = ModuloGestao.Pedidos, DataFato = agora,
                Descricao = $"Pedido #{pedidoId} · {Rotulo(modalidade)}", Origem = new OrigemRef { Tipo = "pedido", Id = pedidoId.ToString() },
                Dados = JsonSerializer.SerializeToElement(dados), Lancamentos = lancamentos,
            }, ct);
            return r.Id;
        }
        catch (GestaoException ex)
        {
            throw new DeliveryDomainException(ex.Status, "VENDA_NAO_REGISTRADA", "A venda nao foi registrada no livro: " + ex.Message);
        }
    }

    /// <summary>Cancelamento autorizado de pedido concluido: estorna a venda (uma vez), com motivo e responsavel.</summary>
    public static async Task<Guid?> EstornarAsync(IAcontecimentosService livro, NpgsqlConnection c, NpgsqlTransaction tx, Guid loja,
        int pedidoId, int responsavelId, string motivo, CancellationToken ct = default)
    {
        if (!await Existe(c, tx, "gestao_acontecimentos")) return null;
        await AcontecimentoSql.TravarChave(c, tx, loja, Chave(pedidoId));
        var venda = await VendaAtual(c, tx, loja, pedidoId);
        if (venda is not { } v || v.Estornada) return null;
        try
        {
            var r = await livro.Estornar(c, tx, loja, responsavelId, v.Id, new EstornoRequest
            {
                ChaveIdempotencia = $"estorno:{Chave(pedidoId)}", Motivo = motivo, ConfirmacaoHumana = true,
            }, ct);
            return r.Id;
        }
        catch (GestaoException ex)
        {
            throw new DeliveryDomainException(ex.Status, ex.Codigo == "JA_ESTORNADO" ? "VENDA_JA_ESTORNADA" : "ESTORNO_NAO_REGISTRADO",
                "O estorno da venda nao foi registrado: " + ex.Message);
        }
    }

    private static Task<VendaRow?> VendaAtual(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int pedidoId) =>
        c.QuerySingleOrDefaultAsync<VendaRow?>(@"
SELECT a.id AS Id, EXISTS (SELECT 1 FROM gestao_acontecimentos e WHERE e.estabelecimento_id = a.estabelecimento_id
                           AND e.corrige_id = a.id AND e.correcao = 'estorno') AS Estornada
  FROM gestao_acontecimentos a
 WHERE a.estabelecimento_id = @loja AND a.tipo = 'venda' AND a.origem_tipo = 'pedido' AND a.origem_id = @id AND a.corrige_id IS NULL;",
            new { loja, id = pedidoId.ToString() }, tx);

    private static async Task<(Guid? Gaveta, bool CaixaAberto, Guid? Maquininha, Guid? Ifood)> Contas(NpgsqlConnection c,
        NpgsqlTransaction tx, Guid loja, DateOnly hoje)
    {
        Guid? gaveta = null;
        var aberto = false;
        if (await Existe(c, tx, "gestao_caixas"))
        {
            gaveta = await c.ExecuteScalarAsync<Guid?>("SELECT conta_gaveta_id FROM gestao_caixas WHERE estabelecimento_id = @loja;", new { loja }, tx);
            aberto = gaveta.HasValue && await c.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM gestao_caixa_dias
 WHERE estabelecimento_id = @loja AND dia = @dia AND fechado_em IS NULL);", new { loja, dia = hoje.ToDateTime(TimeOnly.MinValue) }, tx);
        }
        // Mais de um recebivel do mesmo subtipo: vale o primeiro criado (decisao registrada no relatorio da 6C1-A).
        async Task<Guid?> Recebivel(string subtipo) => await c.ExecuteScalarAsync<Guid?>(@"SELECT id FROM gestao_contas
 WHERE estabelecimento_id = @loja AND tipo = 'recebivel' AND subtipo = @subtipo AND ativo ORDER BY criado_em, id LIMIT 1;",
            new { loja, subtipo }, tx);
        return (gaveta, aberto, await Recebivel("maquininha"), await Recebivel("ifood"));
    }

    private static Task<bool> Existe(NpgsqlConnection c, NpgsqlTransaction tx, string tabela) =>
        c.ExecuteScalarAsync<bool>("SELECT to_regclass(@tabela) IS NOT NULL;", new { tabela }, tx);

    private static string Rotulo(string modalidade) => modalidade switch
    {
        ModalidadeVenda.Salao => "salão",
        ModalidadeVenda.Retirada => "retirada",
        _ => "delivery",
    };

    private sealed class PedidoVenda
    {
        public int Id { get; set; }
        public decimal? Total { get; set; }
        public string? TipoPagamento { get; set; }
        public string? Origem { get; set; }
        public DateTime Agora { get; set; }
    }

    private sealed class VendaRow
    {
        public Guid Id { get; set; }
        public bool Estornada { get; set; }
    }

    private sealed class IfoodVenda
    {
        public decimal? Total { get; set; }
        public decimal? Prepago { get; set; }
        public string? Tipo { get; set; }
    }
}
