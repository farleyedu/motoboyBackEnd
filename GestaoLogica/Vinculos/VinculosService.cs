using System.Data;
using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Vinculos;

public sealed record NovoInsumoRequest(string Nome, string Unidade, string? Categoria);

/// <summary>Uma das três escolhas: insumo da loja, item do catálogo global (importa para a loja) ou novo insumo.</summary>
public sealed record ConfirmarVinculoRequest(Guid? InsumoId, Guid? GlobalId, NovoInsumoRequest? Novo, decimal Fator,
    bool QualquerFornecedor = false);

public sealed record VinculoDto(Guid Id, string FornecedorCnpj, string Descricao, string NomeChave, string UnidadeCupom,
    Guid InsumoId, string? InsumoNome, Guid? GlobalId, string UnidadeBase, decimal Fator, string Estado, int ConfirmadoPor,
    DateTimeOffset ConfirmadoEm, string? MotivoDesfeito);

public sealed record VinculoConfirmadoDto(VinculoDto Vinculo, Conversao Conversao, DivergenciaPreco? Divergencia, bool InsumoCriado);

/// <summary>Sugestão, confirmação (aprendizado local imediato) e desfazer de vínculos de compras. Não mexe em estoque.</summary>
public interface IVinculosService
{
    Task<IReadOnlyList<SugestaoItem>> Sugerir(Guid loja, Guid documentoId, CancellationToken ct);
    Task<VinculoConfirmadoDto> Confirmar(Guid loja, int usuario, Guid documentoId, int linha, ConfirmarVinculoRequest req, CancellationToken ct);
    Task<VinculoDto> Desfazer(Guid loja, int usuario, Guid vinculoId, string? motivo, CancellationToken ct);
    Task<IReadOnlyList<VinculoDto>> Listar(Guid loja, string? busca, bool incluirDesfeitos, CancellationToken ct);
}

public sealed class VinculosService(NpgsqlDataSource dataSource, TimeProvider? relogio = null) : IVinculosService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    private const decimal ConfiancaMesmoFornecedor = 0.98m, ConfiancaEan = 0.95m, ConfiancaOutroFornecedor = 0.9m,
        ConfiancaGlobalEan = 0.92m, ConfiancaGlobalNome = 0.88m, PesoSimilarLoja = 0.75m, PesoSimilarGlobal = 0.7m,
        OutraEmbalagem = 0.03m;

    private sealed record Documento(Guid Id, DocumentoLido Lido, DateTimeOffset CriadoEm);
    private sealed record Insumo(Guid Id, string Nome, string Unidade);
    private sealed record VinculoLinha(Guid Id, string FornecedorCnpj, string NomeChave, string? Ean, Guid InsumoId, Guid? GlobalId,
        string UnidadeBase, decimal Fator, string UnidadeCupom);
    private sealed record Comunidade(string NomeChave, Guid GlobalId, string Status, string Nome, string Unidade, string UnidadeCupom,
        int Lojas, decimal Fator);
    private sealed record Global(Guid Id, string Nome, string Unidade, string? Ean);

    public async Task<IReadOnlyList<SugestaoItem>> Sugerir(Guid loja, Guid documentoId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var doc = await ObterDocumento(c, null, loja, documentoId);
        var itens = doc.Lido.Itens;
        var chaves = itens.Select(i => Chave(i.Descricao)).Distinct().ToArray();
        var eans = itens.Where(i => i.Ean != null).Select(i => i.Ean!).Distinct().ToArray();
        var cnpj = doc.Lido.Fornecedor.Cnpj ?? "";

        var insumos = await TemInsumos(c, null)
            ? (await c.QueryAsync<Insumo>("SELECT id, nome, unidade FROM insumo WHERE estabelecimento_id = @loja AND ativo",
                new { loja })).ToDictionary(i => i.Id)
            : new Dictionary<Guid, Insumo>();
        var meus = (await c.QueryAsync<VinculoLinha>(@"
SELECT id, fornecedor_cnpj AS FornecedorCnpj, nome_chave AS NomeChave, ean, insumo_id AS InsumoId, global_id AS GlobalId,
       unidade_base AS UnidadeBase, fator, unidade_cupom AS UnidadeCupom FROM gestao_vinculos
 WHERE estabelecimento_id = @loja AND estado = 'confirmado' AND (nome_chave = ANY(@chaves) OR ean = ANY(@eans))",
            new { loja, chaves, eans })).ToList();
        var outras = (await c.QueryAsync<Comunidade>(@"
SELECT v.nome_chave AS NomeChave, g.id AS GlobalId, g.status, g.nome, g.unidade, v.unidade_cupom AS UnidadeCupom,
       (SELECT count(DISTINCT o.estabelecimento_id) FROM gestao_vinculos o
         WHERE o.global_id = g.id AND o.nome_chave = v.nome_chave AND o.estado = 'confirmado' AND o.estabelecimento_id <> @loja)::int AS lojas,
       mode() WITHIN GROUP (ORDER BY v.fator) AS fator
  FROM gestao_vinculos v JOIN gestao_catalogo_global g ON g.id = v.global_id
 WHERE v.estado = 'confirmado' AND v.estabelecimento_id <> @loja AND v.nome_chave = ANY(@chaves)
   AND g.status IN ('candidato', 'validado')
 GROUP BY v.nome_chave, g.id, g.status, g.nome, g.unidade, v.unidade_cupom", new { loja, chaves })).ToList();
        var validados = (await c.QueryAsync<Global>(
            "SELECT id, nome, unidade, ean FROM gestao_catalogo_global WHERE status = 'validado'")).ToList();
        // Item global que a loja já trouxe para si: a sugestão aponta para o insumo dela.
        var insumoDoGlobal = (await c.QueryAsync<(Guid GlobalId, Guid InsumoId)>(@"
SELECT DISTINCT ON (global_id) global_id, insumo_id FROM gestao_vinculos
 WHERE estabelecimento_id = @loja AND global_id IS NOT NULL ORDER BY global_id, confirmado_em DESC", new { loja }))
            .ToDictionary(x => x.GlobalId, x => x.InsumoId);

        var resultado = new List<SugestaoItem>();
        var historicos = new Dictionary<Guid, IReadOnlyList<decimal>>();
        foreach (var item in itens)
        {
            var chave = Chave(item.Descricao);
            var candidatas = new List<Sugestao>();
            VinculoLinha? vinculo = null;
            var confVinculo = 0m;
            foreach (var v in meus.Where(v => v.NomeChave == chave || (item.Ean != null && v.Ean == item.Ean)))
            {
                var (conf, motivo) = v.NomeChave != chave ? (ConfiancaEan, "Mesmo código de barras já vinculado pela loja.")
                    : v.FornecedorCnpj == cnpj && cnpj != "" ? (ConfiancaMesmoFornecedor, "A loja já vinculou este item deste fornecedor.")
                    : (ConfiancaOutroFornecedor, "A loja já vinculou este nome (outro fornecedor ou qualquer um).");
                // O fator aprendido é da embalagem: "SAC KRAFT" em PCT (50 un) não vale para "SAC KRAFT" em UN.
                var mesmaEmbalagem = v.UnidadeCupom == Unidade(item);
                if (!mesmaEmbalagem)
                    (conf, motivo) = (conf - OutraEmbalagem, motivo + $" Antes veio em {v.UnidadeCupom}: confira a quantidade.");
                else if (conf > confVinculo) (vinculo, confVinculo) = (v, conf);
                candidatas.Add(new Sugestao(OrigemSugestao.Loja, v.InsumoId, v.GlobalId, NomeInsumo(insumos, v.InsumoId, item.Descricao),
                    v.UnidadeBase, mesmaEmbalagem ? v.Fator : null, conf, motivo));
            }
            // Uma sugestão por item global: o fator só vem se alguma loja já confirmou a mesma embalagem.
            foreach (var o in outras.Where(o => o.NomeChave == chave).GroupBy(o => o.GlobalId)
                         .Select(g => g.FirstOrDefault(o => o.UnidadeCupom == Unidade(item)) ?? (g.First() with { Fator = 0 })))
            {
                var validado = o.Status == "validado";
                candidatas.Add(new Sugestao(validado ? OrigemSugestao.Global : OrigemSugestao.Comunidade,
                    insumoDoGlobal.GetValueOrDefault(o.GlobalId) is var i && i != Guid.Empty ? i : null, o.GlobalId, o.Nome, o.Unidade,
                    o.Fator > 0 ? o.Fator : null,
                    validado ? ConfiancaGlobalNome : VinculoRegras.ConfiancaComunidade(o.Lojas),
                    validado ? "Validado pela curadoria Zippy." : $"Confirmado por {o.Lojas} {(o.Lojas == 1 ? "loja" : "lojas")}, ainda sem curadoria."));
            }
            foreach (var g in validados)
            {
                var porEan = item.Ean != null && g.Ean == item.Ean;
                var s = porEan ? 1 : VinculoRegras.Semelhanca(item.Descricao, g.Nome);
                if (!porEan && s < VinculoRegras.SemelhancaMinima) continue;
                candidatas.Add(new Sugestao(porEan ? OrigemSugestao.Global : OrigemSugestao.Similar,
                    insumoDoGlobal.GetValueOrDefault(g.Id) is var i && i != Guid.Empty ? i : null, g.Id, g.Nome, g.Unidade, null,
                    porEan ? ConfiancaGlobalEan : Math.Round(s * PesoSimilarGlobal, 2),
                    porEan ? "Código de barras do catálogo Zippy." : "Nome parecido no catálogo Zippy."));
            }
            foreach (var ins in insumos.Values)
            {
                var s = VinculoRegras.Semelhanca(item.Descricao, ins.Nome);
                if (s >= VinculoRegras.SemelhancaMinima)
                    candidatas.Add(new Sugestao(OrigemSugestao.Similar, ins.Id, null, ins.Nome, ins.Unidade, null,
                        Math.Round(s * PesoSimilarLoja, 2), "Nome parecido com um insumo da loja."));
            }

            var sugestoes = VinculoRegras.Ordenar(candidatas, item.Descricao);
            var melhor = sugestoes[0].Origem == OrigemSugestao.CriarNovo ? null : sugestoes[0];
            var conversao = UnidadeRegras.Converter(item, melhor?.UnidadeBase, melhor?.Fator);
            DivergenciaPreco? divergencia = null;
            if (conversao is not null && melhor?.InsumoId is { } insumoId)
            {
                if (!historicos.TryGetValue(insumoId, out var hist))
                    historicos[insumoId] = hist = await Historico(c, null, loja, insumoId, doc.Id);
                divergencia = VinculoRegras.Divergencia(VinculoRegras.PrecoBase(item.ValorTotal, conversao.Quantidade), hist,
                    conversao.Fator, conversao.Unidade);
            }
            resultado.Add(new SugestaoItem(item.Linha, item.Descricao, chave, sugestoes, conversao,
                conversao is null ? UnidadeRegras.Pergunta(item, melhor?.UnidadeBase) : null, divergencia, vinculo?.Id));
        }
        return resultado;
    }

    public async Task<VinculoConfirmadoDto> Confirmar(Guid loja, int usuario, Guid documentoId, int linha, ConfirmarVinculoRequest req,
        CancellationToken ct)
    {
        if (req.Fator <= 0) throw Invalido("Informe quanto vem em 1 unidade do cupom (fator maior que zero).");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var doc = await ObterDocumento(c, tx, loja, documentoId);
        var item = doc.Lido.Itens.FirstOrDefault(i => i.Linha == linha)
            ?? throw new GestaoException("NAO_ENCONTRADO", $"O documento não tem a linha {linha}.", 404);
        if (!await TemInsumos(c, tx))
            throw Invalido("Os insumos da loja ainda não existem: ative a Gestão e Inteligência para vincular compras.");
        var agora = _relogio.GetUtcNow();

        var (insumo, globalId, criado) = await ResolverAlvo(c, tx, loja, usuario, item, req, agora);
        var chave = Chave(item.Descricao);
        var cnpj = req.QualquerFornecedor ? "" : doc.Lido.Fornecedor.Cnpj ?? "";
        var fator = Math.Round(req.Fator, 6);

        var atual = await c.QuerySingleOrDefaultAsync<VinculoLinha>(@"
SELECT id, fornecedor_cnpj AS FornecedorCnpj, nome_chave AS NomeChave, ean, insumo_id AS InsumoId, global_id AS GlobalId,
       unidade_base AS UnidadeBase, fator, unidade_cupom AS UnidadeCupom FROM gestao_vinculos
 WHERE estabelecimento_id = @loja AND fornecedor_cnpj = @cnpj AND nome_chave = @chave AND unidade_cupom = @unidadeCupom
   AND estado = 'confirmado' FOR UPDATE", new { loja, cnpj, chave, unidadeCupom = Unidade(item) }, tx);
        Guid vinculoId;
        if (atual is not null && atual.InsumoId == insumo.Id && atual.Fator == fator)
            vinculoId = atual.Id; // mesmo vínculo: confirmar de novo não duplica
        else
        {
            if (atual is not null)
                await c.ExecuteAsync(@"
UPDATE gestao_vinculos SET estado = 'desfeito', desfeito_por = @usuario, desfeito_em = @agora,
       motivo_desfeito = 'Substituído por um novo vínculo.' WHERE id = @id", new { id = atual.Id, usuario, agora }, tx);
            vinculoId = Guid.NewGuid();
            await c.ExecuteAsync(@"
INSERT INTO gestao_vinculos (id, estabelecimento_id, fornecedor_cnpj, nome_chave, descricao, unidade_cupom, ean, insumo_id, global_id,
    unidade_base, fator, documento_id, confirmado_por, confirmado_em)
VALUES (@vinculoId, @loja, @cnpj, @chave, @descricao, @unidadeCupom, @Ean, @insumoId, @globalId, @unidade, @fator, @documentoId,
    @usuario, @agora)", new
            {
                vinculoId, loja, cnpj, chave, descricao = Cortar(item.Descricao, 200), unidadeCupom = Unidade(item), item.Ean,
                insumoId = insumo.Id, globalId, unidade = insumo.Unidade, fator, documentoId, usuario, agora,
            }, tx);
        }

        var quantidade = Math.Round(item.Quantidade * fator, 4);
        var conversao = new Conversao(quantidade, insumo.Unidade, fator, "vinculo",
            $"{item.Quantidade:0.####} {Unidade(item)} × {fator:0.####} {insumo.Unidade} = {quantidade:0.####} {insumo.Unidade}");
        var preco = VinculoRegras.PrecoBase(item.ValorTotal, quantidade);
        var divergencia = VinculoRegras.Divergencia(preco, await Historico(c, tx, loja, insumo.Id, doc.Id), fator, insumo.Unidade);
        if (quantidade > 0)
            await c.ExecuteAsync(@"
INSERT INTO gestao_vinculo_precos (id, estabelecimento_id, vinculo_id, insumo_id, documento_id, linha, quantidade_base, valor_total,
    preco_base, observado_em, criado_por, criado_em)
VALUES (@id, @loja, @vinculoId, @insumoId, @documentoId, @linha, @quantidade, @ValorTotal, @preco, @observado, @usuario, @agora)
ON CONFLICT (vinculo_id, documento_id, linha) WHERE documento_id IS NOT NULL DO NOTHING", new
            {
                id = Guid.NewGuid(), loja, vinculoId, insumoId = insumo.Id, documentoId, linha, quantidade, item.ValorTotal, preco,
                observado = (doc.Lido.DataEmissao ?? doc.CriadoEm).ToUniversalTime(), usuario, agora,
            }, tx);
        await tx.CommitAsync(ct);
        return new VinculoConfirmadoDto((await Listar(c, loja, vinculoId, null, true)).Single(), conversao, divergencia, criado);
    }

    public async Task<VinculoDto> Desfazer(Guid loja, int usuario, Guid vinculoId, string? motivo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw Invalido("Diga por que o vínculo está sendo desfeito.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var n = await c.ExecuteAsync(@"
UPDATE gestao_vinculos SET estado = 'desfeito', desfeito_por = @usuario, desfeito_em = @agora, motivo_desfeito = @motivo
 WHERE estabelecimento_id = @loja AND id = @vinculoId AND estado = 'confirmado'",
            new { loja, vinculoId, usuario, agora = _relogio.GetUtcNow(), motivo = Cortar(motivo.Trim(), 300) });
        var v = (await Listar(c, loja, vinculoId, null, true)).SingleOrDefault()
            ?? throw new GestaoException("NAO_ENCONTRADO", "Vínculo não encontrado.", 404);
        if (n == 0) throw new GestaoException("JA_ESTORNADO", "Este vínculo já estava desfeito.", 409);
        return v;
    }

    public async Task<IReadOnlyList<VinculoDto>> Listar(Guid loja, string? busca, bool incluirDesfeitos, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await Listar(c, loja, null, busca, incluirDesfeitos);
    }

    private static async Task<IReadOnlyList<VinculoDto>> Listar(NpgsqlConnection c, Guid loja, Guid? id, string? busca, bool incluirDesfeitos)
    {
        var temInsumos = await TemInsumos(c, null);
        var termo = string.IsNullOrWhiteSpace(busca) ? null : "%" + DocumentoRegras.NomeChave(busca) + "%";
        var nome = temInsumos ? "(SELECT i.nome FROM insumo i WHERE i.estabelecimento_id = v.estabelecimento_id AND i.id = v.insumo_id)" : "NULL";
        var linhas = await c.QueryAsync<(Guid Id, string FornecedorCnpj, string Descricao, string NomeChave, string UnidadeCupom,
            Guid InsumoId, string? InsumoNome, Guid? GlobalId, string UnidadeBase, decimal Fator, string Estado, int ConfirmadoPor,
            DateTime ConfirmadoEm, string? MotivoDesfeito)>($@"
SELECT v.id, v.fornecedor_cnpj, v.descricao, v.nome_chave, v.unidade_cupom, v.insumo_id, {nome}, v.global_id, v.unidade_base,
       v.fator, v.estado, v.confirmado_por, v.confirmado_em, v.motivo_desfeito
  FROM gestao_vinculos v
 WHERE v.estabelecimento_id = @loja AND (@id::uuid IS NULL OR v.id = @id) AND (@todos OR v.estado = 'confirmado')
   AND (@termo::text IS NULL OR v.nome_chave LIKE @termo OR upper(coalesce({nome}, '')) LIKE @termo)
 ORDER BY v.confirmado_em DESC LIMIT 500", new { loja, id, todos = incluirDesfeitos || id is not null, termo });
        return linhas.Select(l => new VinculoDto(l.Id, l.FornecedorCnpj, l.Descricao, l.NomeChave, l.UnidadeCupom, l.InsumoId,
            l.InsumoNome, l.GlobalId, l.UnidadeBase, l.Fator, l.Estado, l.ConfirmadoPor,
            new DateTimeOffset(DateTime.SpecifyKind(l.ConfirmadoEm, DateTimeKind.Utc)), l.MotivoDesfeito)).ToList();
    }

    /// <summary>
    /// Insumo da loja que recebe o vínculo. Item global vira da loja ao ser escolhido (regra 6); insumo novo ou sem item
    /// global vira candidato para a curadoria, a menos que já exista um validado com o mesmo nome e unidade.
    /// </summary>
    private static async Task<(Insumo Insumo, Guid? GlobalId, bool Criado)> ResolverAlvo(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid loja, int usuario, ItemLido item, ConfirmarVinculoRequest req, DateTimeOffset agora)
    {
        if (req.InsumoId is { } insumoId)
        {
            var insumo = await c.QuerySingleOrDefaultAsync<Insumo>(
                "SELECT id, nome, unidade FROM insumo WHERE estabelecimento_id = @loja AND id = @insumoId AND ativo",
                new { loja, insumoId }, tx) ?? throw Invalido("Insumo não encontrado nesta loja.");
            var global = await c.ExecuteScalarAsync<Guid?>(@"
SELECT global_id FROM gestao_vinculos WHERE estabelecimento_id = @loja AND insumo_id = @insumoId AND global_id IS NOT NULL
 ORDER BY confirmado_em DESC LIMIT 1", new { loja, insumoId }, tx)
                ?? await GlobalPara(c, tx, loja, usuario, insumo.Nome, insumo.Unidade, null, item.Ean, agora);
            return (insumo, global, false);
        }
        if (req.GlobalId is { } pedido)
        {
            var g = await c.QuerySingleOrDefaultAsync<(Guid Id, string Nome, string Unidade, string? Categoria, string Status, Guid? MescladoEm)>(
                "SELECT id, nome, unidade, categoria, status, mesclado_em FROM gestao_catalogo_global WHERE id = @pedido", new { pedido }, tx);
            if (g.Id == Guid.Empty || g.Status == "rejeitado") throw Invalido("Item do catálogo não encontrado.");
            if (g.Status == "mesclado")
                g = await c.QuerySingleAsync<(Guid, string, string, string?, string, Guid?)>(
                    "SELECT id, nome, unidade, categoria, status, mesclado_em FROM gestao_catalogo_global WHERE id = @d",
                    new { d = g.MescladoEm }, tx);
            var existente = await c.ExecuteScalarAsync<Guid?>(@"
SELECT v.insumo_id FROM gestao_vinculos v JOIN insumo i ON i.estabelecimento_id = v.estabelecimento_id AND i.id = v.insumo_id AND i.ativo
 WHERE v.estabelecimento_id = @loja AND v.global_id = @id ORDER BY v.confirmado_em DESC LIMIT 1", new { loja, g.Id }, tx);
            if (existente is { } ja)
                return (await c.QuerySingleAsync<Insumo>("SELECT id, nome, unidade FROM insumo WHERE id = @ja", new { ja }, tx), g.Id, false);
            return (await CriarInsumo(c, tx, loja, usuario, g.Nome, g.Unidade, g.Categoria, agora), g.Id, true);
        }
        if (req.Novo is { } novo)
        {
            if (string.IsNullOrWhiteSpace(novo.Nome)) throw Invalido("Informe o nome do novo insumo.");
            if (!UnidadeBase.Valida(novo.Unidade)) throw Invalido("A unidade do insumo deve ser g, ml ou un.");
            var insumo = await CriarInsumo(c, tx, loja, usuario, novo.Nome.Trim(), novo.Unidade, novo.Categoria, agora);
            return (insumo, await GlobalPara(c, tx, loja, usuario, insumo.Nome, insumo.Unidade, novo.Categoria, item.Ean, agora), true);
        }
        throw Invalido("Escolha um insumo da loja, um item do catálogo ou cadastre um novo.");
    }

    private static async Task<Guid> GlobalPara(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, string nome,
        string unidade, string? categoria, string? ean, DateTimeOffset agora)
    {
        var nomeChave = Chave(nome);
        if (await c.ExecuteScalarAsync<Guid?>(@"
SELECT id FROM gestao_catalogo_global WHERE status = 'validado' AND ((nome_chave = @nomeChave AND unidade = @unidade) OR ean = @ean)
 ORDER BY (ean = @ean) DESC NULLS LAST LIMIT 1", new { nomeChave, unidade, ean }, tx) is { } validado)
            return validado;
        var id = Guid.NewGuid();
        await c.ExecuteAsync(@"
INSERT INTO gestao_catalogo_global (id, nome, nome_chave, unidade, categoria, ean, criado_por_loja, criado_por, criado_em)
VALUES (@id, @nome, @nomeChave, @unidade, @categoria, @ean, @loja, @usuario, @agora)",
            new { id, nome = Cortar(nome, 120), nomeChave, unidade, categoria = Cortar(categoria, 60), ean, loja, usuario, agora }, tx);
        return id;
    }

    private static async Task<Insumo> CriarInsumo(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, string nome,
        string unidade, string? categoria, DateTimeOffset agora)
    {
        var insumo = new Insumo(Guid.NewGuid(), Cortar(nome, 120)!, unidade);
        await c.ExecuteAsync(@"
INSERT INTO insumo (id, estabelecimento_id, nome, unidade, categoria, criado_por, criado_em, atualizado_em)
VALUES (@Id, @loja, @Nome, @Unidade, @categoria, @usuario, @agora, @agora)",
            new { insumo.Id, loja, insumo.Nome, insumo.Unidade, categoria = Cortar(categoria, 60), usuario, agora }, tx);
        return insumo;
    }

    private static async Task<Documento> ObterDocumento(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid id)
    {
        var row = await c.QuerySingleOrDefaultAsync<(Guid Id, string Leitura, DateTime CriadoEm)>(
            "SELECT id, leitura::text, criado_em FROM gestao_documentos WHERE estabelecimento_id = @loja AND id = @id", new { loja, id }, tx);
        if (row.Id == Guid.Empty) throw new GestaoException("NAO_ENCONTRADO", "Documento não encontrado.", 404);
        return new Documento(row.Id, JsonSerializer.Deserialize<DocumentoLido>(row.Leitura, Json)!,
            new DateTimeOffset(DateTime.SpecifyKind(row.CriadoEm, DateTimeKind.Utc)));
    }

    /// <summary>Últimos 12 preços por unidade base do insumo, sem o documento em análise.</summary>
    private static async Task<IReadOnlyList<decimal>> Historico(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid insumoId, Guid documentoId) =>
        (await c.QueryAsync<decimal>(@"
SELECT p.preco_base FROM gestao_vinculo_precos p JOIN gestao_vinculos v ON v.id = p.vinculo_id AND v.estado = 'confirmado'
 WHERE p.estabelecimento_id = @loja AND p.insumo_id = @insumoId AND p.documento_id IS DISTINCT FROM @documentoId
 ORDER BY p.observado_em DESC LIMIT 12", new { loja, insumoId, documentoId }, tx)).ToList();

    private static async Task<bool> TemInsumos(NpgsqlConnection c, NpgsqlTransaction? tx) =>
        await c.ExecuteScalarAsync<bool>("SELECT to_regclass('insumo') IS NOT NULL", transaction: tx);

    private static string NomeInsumo(Dictionary<Guid, Insumo> insumos, Guid id, string reserva) =>
        insumos.TryGetValue(id, out var i) ? i.Nome : reserva;

    private static string Chave(string texto) => Cortar(DocumentoRegras.NomeChave(texto), 160)!;

    private static string Unidade(ItemLido item) => Cortar(item.Unidade.Trim().ToUpperInvariant(), 10)!;

    private static string? Cortar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
