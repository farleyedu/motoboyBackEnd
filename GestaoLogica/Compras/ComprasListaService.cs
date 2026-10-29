using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Compras;

public sealed record CompraManualItem(string? Descricao, decimal Quantidade, string? Unidade, decimal ValorUnitario, decimal Desconto = 0);

/// <summary>
/// Compra lançada à mão (sem foto nem XML). Vira um documento de origem <c>manual</c> e segue a mesma regra da compra lida:
/// entra no financeiro na hora e os itens ficam pendentes de conferência. <c>ChaveIdempotencia</c>: repetir o envio devolve a
/// mesma compra. Com CNPJ e número, o mesmo documento não entra duas vezes (DUPLICADO).
/// </summary>
public sealed record CompraManualRequest(string? ChaveIdempotencia, string? FornecedorNome, string? FornecedorCnpj, DateOnly? Data,
    string? Numero, IReadOnlyList<CompraManualItem>? Itens, string? Forma, Guid? ContaId, int Parcelas = 1, DateOnly? Vencimento = null,
    DateOnly? DataPagamento = null, string? Categoria = null, string? Finalidade = null);

/// <summary>Lista de compras do mês (pelo livro) e compra à mão. A compra continua sendo a do <see cref="IComprasService"/>.</summary>
public interface IComprasListaService
{
    Task<IReadOnlyList<CompraDto>> Listar(Guid loja, DateOnly? mes, DateOnly? dia, CancellationToken ct);
    Task<CompraDto> RegistrarManual(Guid loja, int usuario, CompraManualRequest req, CancellationToken ct);
}

public sealed class ComprasListaService(NpgsqlDataSource dataSource, IDocumentosService documentos, IComprasService compras,
    TimeProvider? relogio = null) : IComprasListaService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;
    public const int LimiteLista = 100;

    /// <summary>
    /// Compras em que o fato, um vencimento ou um pagamento cai no mês (ou no dia), mais recentes primeiro. Estornadas também
    /// aparecem (marcadas), porque nada se apaga.
    /// </summary>
    public async Task<IReadOnlyList<CompraDto>> Listar(Guid loja, DateOnly? mes, DateOnly? dia, CancellationToken ct)
    {
        if (mes is null && dia is null) throw new GestaoException("CAMPO_INVALIDO", "Escolha o mês.", 422);
        var inicio = mes is { } m ? AcontecimentoRegras.Mes(m) : (DateOnly?)null;
        if (dia is { } d && inicio is { } i && AcontecimentoRegras.Mes(d) != i)
            throw new GestaoException("CAMPO_INVALIDO", "O dia precisa estar dentro do mês escolhido.", 422);
        List<string> ids;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            var fuso = await c.ExecuteScalarAsync<string?>("SELECT timezone_iana FROM estabelecimentos WHERE id = @loja", new { loja })
                ?? "America/Sao_Paulo";
            ids = (await c.QueryAsync<string>(@"
SELECT a.origem_id FROM gestao_acontecimentos a
 WHERE a.estabelecimento_id = @loja AND a.tipo = 'compra' AND a.origem_tipo = 'compra' AND a.corrige_id IS NULL
   AND (@mes::date IS NULL OR a.mes_referencia = @mes OR EXISTS (
        SELECT 1 FROM gestao_lancamentos l WHERE l.estabelecimento_id = a.estabelecimento_id AND l.acontecimento_id = a.id
           AND (date_trunc('month', l.data_vencimento)::date = @mes OR date_trunc('month', l.data_pagamento)::date = @mes)))
   AND (@dia::date IS NULL OR (a.data_fato AT TIME ZONE @fuso)::date = @dia OR EXISTS (
        SELECT 1 FROM gestao_lancamentos l WHERE l.estabelecimento_id = a.estabelecimento_id AND l.acontecimento_id = a.id
           AND (l.data_vencimento = @dia OR l.data_pagamento = @dia)))
 ORDER BY a.data_fato DESC, a.registrado_em DESC LIMIT @limite", new { loja, mes = inicio, dia, fuso, limite = LimiteLista })).ToList();
        }
        var lista = new List<CompraDto>(ids.Count);
        foreach (var id in ids)
            if (Guid.TryParse(id, out var documentoId) && await compras.Obter(loja, documentoId, ct) is { } compra) lista.Add(compra);
        return lista;
    }

    public async Task<CompraDto> RegistrarManual(Guid loja, int usuario, CompraManualRequest req, CancellationToken ct)
    {
        TimeZoneInfo fuso;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
            fuso = HorarioRegras.ResolverFuso(await c.ExecuteScalarAsync<string?>(
                "SELECT timezone_iana FROM estabelecimentos WHERE id = @loja", new { loja }));
        var hoje = AcontecimentoRegras.DiaLocal(_relogio.GetUtcNow(), fuso);
        var lido = ComprasManualRegras.Montar(req, hoje, fuso);
        ComprasManualRegras.ValidarPagamento(req);

        // Repetir o mesmo envio (mesma chave) não cria outro documento: devolve a compra (ou termina o registro que caiu no meio).
        var hash = string.IsNullOrWhiteSpace(req.ChaveIdempotencia) ? null
            : DocumentoRegras.Hash(System.Text.Encoding.UTF8.GetBytes($"manual:{loja}:{req.ChaveIdempotencia.Trim()}"));
        Guid? documentoId = null;
        if (hash is not null)
            await using (var c = await dataSource.OpenConnectionAsync(ct))
                documentoId = await c.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM gestao_documentos WHERE estabelecimento_id = @loja AND hash_arquivo = @hash", new { loja, hash });
        if (documentoId is { } ja && await compras.Obter(loja, ja, ct) is { } existente) return existente;
        documentoId ??= (await documentos.Registrar(loja, usuario, lido, hash, ct)).Id;

        return await compras.Registrar(loja, usuario, new RegistrarCompraRequest(documentoId.Value, req.Forma!, req.ContaId,
            Math.Max(1, req.Parcelas), req.Vencimento, req.DataPagamento, req.Categoria, req.Finalidade), ct);
    }
}

/// <summary>Regras puras da compra à mão: monta o documento e confere o pagamento antes de gravar qualquer coisa.</summary>
public static class ComprasManualRegras
{
    public const int MaxItens = 200;

    public static DocumentoLido Montar(CompraManualRequest req, DateOnly hoje, TimeZoneInfo fuso)
    {
        var nome = (req.FornecedorNome ?? "").Trim();
        if (nome.Length < 2) throw Invalido("Informe o fornecedor.");
        if (req.Data is not { } data) throw Invalido("Informe a data da compra.");
        if (data > hoje) throw Invalido("A data da compra não pode estar no futuro.");
        var entrada = (req.Itens ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Descricao)).ToList();
        if (entrada.Count == 0) throw Invalido("Inclua pelo menos um item.");
        if (entrada.Count > MaxItens) throw Invalido($"Uma compra aceita até {MaxItens} itens.");
        var itens = new List<ItemLido>();
        foreach (var (i, n) in entrada.Select((i, n) => (i, n + 1)))
        {
            if (i.Quantidade <= 0) throw Invalido($"Item {n}: a quantidade deve ser maior que zero.");
            if (i.ValorUnitario < 0 || i.Desconto < 0) throw Invalido($"Item {n}: valor não pode ser negativo.");
            var total = Math.Round(i.Quantidade * i.ValorUnitario - i.Desconto, 2);
            if (total < 0) throw Invalido($"Item {n}: o desconto é maior que o valor.");
            var unidade = string.IsNullOrWhiteSpace(i.Unidade) ? "UN" : i.Unidade.Trim().ToUpperInvariant();
            itens.Add(new ItemLido(n, Cortar(i.Descricao!.Trim(), 200), i.Quantidade, unidade.Length > 10 ? unidade[..10] : unidade,
                i.ValorUnitario, i.Desconto, total));
        }
        var soma = itens.Sum(i => i.ValorTotal);
        if (soma <= 0) throw Invalido("O total da compra precisa ser maior que zero.");
        // Meio-dia no fuso da loja: a data escolhida é o dia do fato, sem escorregar de dia pelo fuso.
        var meioDia = data.ToDateTime(new TimeOnly(12, 0));
        var emissao = new DateTimeOffset(meioDia, fuso.GetUtcOffset(meioDia));
        var numero = string.IsNullOrWhiteSpace(req.Numero) ? null : Cortar(req.Numero.Trim(), 20);
        return new DocumentoLido(OrigemDocumento.Manual, new FornecedorLido(DocumentoRegras.SoDigitos(req.FornecedorCnpj) is { Length: 14 } cnpj ? cnpj : null,
            Cortar(nome, 160)), emissao, numero, null, null, itens, itens.Sum(i => i.Desconto), soma, ["Lançada à mão"]);
    }

    /// <summary>Mesmas regras do registro da compra, conferidas antes de gravar o documento (que é imutável).</summary>
    public static void ValidarPagamento(CompraManualRequest req)
    {
        switch (req.Forma)
        {
            case FormaCompra.APagar: break;
            case FormaCompra.Pago:
                if (req.ContaId is null) throw Invalido("Diga de qual conta o dinheiro saiu.");
                if (req.Parcelas > 1) throw Invalido("Compra já paga não tem parcelas.");
                break;
            case FormaCompra.Cartao:
                if (req.ContaId is null) throw Invalido("Escolha o cartão da empresa.");
                break;
            default: throw Invalido("Forma de pagamento deve ser a_pagar, pago ou cartao.");
        }
    }

    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
