using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Sessao;
using Dapper;
using Npgsql;
using R = APIBack.Mesas.Pedidos.PedidosRegras;

namespace APIBack.Mesas.Pedidos;

/// <summary>Linha de leitura da cozinha (um item com o que o cartão precisa).</summary>
public sealed class CozinhaLinha
{
    public Guid ItemId { get; set; }
    public Guid PedidoId { get; set; }
    public Guid SessaoId { get; set; }
    public Guid MesaId { get; set; }
    public string MesaNumero { get; set; } = "";
    public int Pessoas { get; set; }
    public string Origem { get; set; } = "garcom";
    public DateTime PedidoCriadoEm { get; set; }
    public bool SegurarPrincipal { get; set; }
    public DateTime? SoltoEm { get; set; }
    public Guid ProdutoId { get; set; }
    public string Nome { get; set; } = "";
    public string? Foto { get; set; }
    public decimal Qtd { get; set; }
    public string Estado { get; set; } = "";
    public string EstacaoId { get; set; } = R.ProducaoGeral;
    public short EtapaAtual { get; set; }
    public string EtapasJson { get; set; } = "[]";
    public string? Obs { get; set; }
    public string? Alergia { get; set; }
    public string? QuemLeva { get; set; }
    public string TipoPrato { get; set; } = "outro";
    public int Refazer { get; set; }
    public string? Cliente { get; set; }
    public Guid? PreparoPor { get; set; }
    public string? PreparoNome { get; set; }
    public DateTime CriadoEm { get; set; }
    public DateTime? TFila { get; set; }
    public DateTime? TPronto { get; set; }
    public DateTime? AvisadoEm { get; set; }
    public DateTime? PegouEm { get; set; }
    public string? PegouNome { get; set; }
    public Guid? LevaGarcomId { get; set; }
    public Guid? SessaoGarcomId { get; set; }
    public string? GarcomNome { get; set; }
    public bool EsperandoGerente { get; set; }
    public bool EntregaDireta { get; set; }
    public bool EntregaPresumida { get; set; }
}

/// <summary>Montagem dos cartões da cozinha (protótipo <c>pedidosDaCozinha</c> e <c>totalParaFazer</c>). Puro: os testes chamam direto.</summary>
public static class CozinhaRegras
{
    private static readonly string[] Fazendo = { "em_fila", "em_preparo" };
    private static readonly string[] AntesDeSair = { "rascunho", "aguardando_aprovacao", "em_fila", "em_preparo" };

    public static bool Segurado(CozinhaLinha it, IReadOnlyList<CozinhaLinha> doPedido) =>
        it.SegurarPrincipal && it.SoltoEm is null && it.TipoPrato == "principal" && it.Estado == "em_fila"
        && doPedido.Any(x => x.TipoPrato == "entrada" && AntesDeSair.Contains(x.Estado));

    public static ItemCozinhaDto Item(CozinhaLinha i, IReadOnlyList<CozinhaLinha> doPedido) => new(i.ItemId, i.PedidoId, i.ProdutoId, i.Nome, i.Foto,
        i.Qtd, i.Estado, i.EstacaoId, i.EtapaAtual, JsonSerializer.Deserialize<List<Etapa>>(i.EtapasJson, MesasComandos.Json) ?? new(), i.Obs, i.Alergia,
        i.QuemLeva, i.TipoPrato, i.Refazer, i.Cliente, i.PreparoPor, i.PreparoNome, i.TFila, i.TPronto, i.AvisadoEm, i.PegouEm, i.PegouNome,
        Segurado(i, doPedido), i.EsperandoGerente, i.EntregaDireta, i.EntregaPresumida);

    /// <summary>
    /// Um cartão por pedido e coluna: <c>novos</c> (nada começou) · <c>preparando</c> · <c>pronto</c> (faixas <c>garcom</c> e <c>cliente</c>).
    /// Vermelho (acima de 1,5× o tempo do prato) sobe; depois o mais antigo primeiro. <paramref name="linhas"/> traz todos os itens dos pedidos.
    /// </summary>
    public static List<CartaoCozinhaDto> Cartoes(IReadOnlyList<CozinhaLinha> linhas, string? estacao, DateTime agora, JsonObject cfg,
        IReadOnlyDictionary<Guid, List<RecadoDto>>? recados = null)
    {
        var todas = string.IsNullOrEmpty(estacao) || estacao == "todas";
        var saida = new List<CartaoCozinhaDto>();
        foreach (var g in linhas.GroupBy(l => l.PedidoId))
        {
            var doPed = g.ToList();
            var meus = doPed.Where(i => R.Ativos.Contains(i.Estado) && (todas || i.EstacaoId == estacao)).ToList();
            if (meus.Count == 0) continue;
            var outros = todas ? new List<OutroSetorDto>() : doPed
                .Where(i => i.EstacaoId != estacao && Fazendo.Contains(i.Estado))
                .GroupBy(i => i.EstacaoId).Select(x => new OutroSetorDto(x.Key, x.Sum(i => i.Qtd))).ToList();
            var faz = meus.Where(i => Fazendo.Contains(i.Estado)).ToList();
            var rec = recados is not null && recados.TryGetValue(g.Key, out var r) ? r : new List<RecadoDto>();

            CartaoCozinhaDto Cartao(List<CozinhaLinha> itens, string coluna, string? faixa)
            {
                var p = itens[0];
                var ini = itens.Min(i => i.TFila ?? i.CriadoEm);
                var esperado = itens.Max(i => R.TempoEsperado(cfg, i.EstacaoId));
                var desdePronto = itens.Min(i => i.TPronto ?? agora);
                var minutos = Min(agora - (coluna == "pronto" ? desdePronto : ini));
                var espera = Min(agora - ini);
                var seg = itens.Count(i => Segurado(i, doPed));
                var naFila = itens.Count(i => i.Estado == "em_fila");
                Guid? garcom = faixa == "garcom" ? itens.Select(i => i.LevaGarcomId).FirstOrDefault(x => x is not null) ?? p.SessaoGarcomId : null;
                int? avisado = coluna == "pronto" ? Min(agora - itens.Max(i => i.AvisadoEm ?? i.TPronto ?? agora)) : null;
                return new CartaoCozinhaDto($"{g.Key}|{(todas ? "todas" : estacao)}|{faixa ?? coluna}", g.Key, p.MesaId, p.MesaNumero, p.Pessoas, p.Origem,
                    p.PedidoCriadoEm, coluna, faixa, itens.Select(i => Item(i, doPed)).ToList(), outros, minutos, espera, esperado,
                    coluna == "pronto" ? "verde" : R.Cor(espera, esperado), seg > 0 && seg == naFila, p.SegurarPrincipal, p.SoltoEm, garcom,
                    faixa == "garcom" ? p.GarcomNome : null, avisado, rec, !R.TemPainel(cfg, p.EstacaoId));
            }

            if (faz.Count > 0)
                saida.Add(Cartao(meus, faz.Any(i => i.Estado == "em_preparo") || faz.Count < meus.Count ? "preparando" : "novos", null));
            else
            {
                var gl = meus.Where(i => i.Estado == "pronto").ToList();
                var cl = meus.Where(i => i.Estado == "aguardando_retirada").ToList();
                if (gl.Count > 0) saida.Add(Cartao(gl, "pronto", "garcom"));
                if (cl.Count > 0) saida.Add(Cartao(cl, "pronto", "cliente"));
            }
        }
        static int K(CartaoCozinhaDto c) => c.Coluna != "pronto" && c.Cor == "vermelho" ? 0 : 1;
        return saida.OrderBy(K).ThenBy(c => c.Itens.Min(i => i.TFila ?? c.CriadoEm)).ToList();
    }

    public static List<TotalItemDto> Total(IEnumerable<CozinhaLinha> linhas, string? estacao)
    {
        var todas = string.IsNullOrEmpty(estacao) || estacao == "todas";
        return linhas.Where(i => Fazendo.Contains(i.Estado) && (todas || i.EstacaoId == estacao))
            .GroupBy(i => i.ProdutoId)
            .Select(g => new TotalItemDto(g.Key, g.First().Nome, g.Sum(i => i.Qtd), g.Select(i => i.PedidoId).Distinct().ToList()))
            .OrderByDescending(t => t.Qtd).ToList();
    }

    /// <summary>Faixa de espera para o cliente (nunca minutos exatos).</summary>
    public static string Faixa(int minutos) => minutos <= 5 ? "até 5 min" : minutos <= 10 ? "5 a 10 min" : minutos <= 20 ? "10 a 20 min" : "mais de 20 min";

    private static int Min(TimeSpan t) => Math.Max(0, (int)Math.Floor(t.TotalMinutes));
}

/// <summary>Leituras da cozinha e do garçom (CONTRATO-API §4.1, P08). O garçom só vê as mesas dele (escopo "meu").</summary>
public sealed class CozinhaService(NpgsqlDataSource source)
{
    private const string LinhaSelect = """
        SELECT i.id AS item_id, i.pedido_id, i.sessao_id, i.mesa_id, m.numero AS mesa_numero,
               GREATEST(s.ocupantes_anonimos + (SELECT count(*) FROM mesa_pessoa pe WHERE pe.sessao_id = s.id AND pe.saiu_em IS NULL), 0) AS pessoas,
               p.origem, p.criado_em AS pedido_criado_em, p.segurar_principal, p.solto_em, i.produto_id, i.nome, i.foto_url AS foto, i.qtd, i.estado,
               i.estacao_id, i.etapa_atual, i.etapas::text AS etapas_json, i.obs, i.alergia, i.quem_leva, i.tipo_prato, i.refazer,
               (SELECT c.nome FROM mesa_item_parte ip JOIN mesa_comanda c ON c.id = ip.comanda_id
                 WHERE ip.item_id = i.id AND c.tipo = 'individual' ORDER BY ip.percentual DESC LIMIT 1) AS cliente,
               i.preparo_por, pp.nome::text AS preparo_nome, i.criado_em, i.t_fila, i.t_pronto, i.avisado_em, i.pegou_em, i.pegou_por_nome AS pegou_nome,
               i.leva_garcom_id, s.garcom_id AS sessao_garcom_id, gp.nome::text AS garcom_nome,
               EXISTS (SELECT 1 FROM mesa_solicitacao so WHERE so.item_id = i.id AND so.estado = 'pendente') AS esperando_gerente,
               i.entrega_direta, i.entrega_presumida
          FROM mesa_item i
          JOIN mesa_pedido p ON p.id = i.pedido_id
          JOIN mesa_sessao s ON s.id = i.sessao_id
          JOIN mesa m ON m.id = i.mesa_id
          LEFT JOIN gestao_pessoas pp ON pp.id = i.preparo_por
          LEFT JOIN gestao_pessoas gp ON gp.id = COALESCE(i.leva_garcom_id, s.garcom_id)
        """;

    public async Task<CozinhaDto> CozinhaAsync(MesasContexto ctx, string? estacao, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var (cfg, agora, garcom) = await Base(c, ctx, ct);
        var linhas = (await c.QueryAsync<CozinhaLinha>(new CommandDefinition(LinhaSelect + """
             WHERE i.estabelecimento_id = @loja
               AND i.pedido_id IN (SELECT a.pedido_id FROM mesa_item a WHERE a.estabelecimento_id = @loja
                                     AND a.estado IN ('em_fila', 'em_preparo', 'pronto', 'aguardando_retirada'))
               AND (CAST(@garcom AS uuid) IS NULL OR s.garcom_id = @garcom OR i.leva_garcom_id = @garcom)
            """, new { loja = ctx.Loja, garcom }, cancellationToken: ct))).ToList();
        var pedidos = linhas.Select(l => l.PedidoId).Distinct().ToArray();
        var recados = (await c.QueryAsync<(Guid PedidoId, string Tipo, string? Texto, string? Para, string? Nome, DateTime Quando)>(new CommandDefinition("""
            SELECT pedido_id, tipo, texto, para, por_nome, quando FROM mesa_recado WHERE pedido_id = ANY(@pedidos) ORDER BY quando
            """, new { pedidos }, cancellationToken: ct)))
            .GroupBy(r => r.PedidoId).ToDictionary(g => g.Key, g => g.Select(r => new RecadoDto(r.Tipo, r.Texto, r.Para, r.Nome, r.Quando)).ToList());
        var cartoes = CozinhaRegras.Cartoes(linhas, estacao, agora, cfg, recados);
        return new CozinhaDto(cartoes, Estacoes(cfg, linhas, agora), agora);
    }

    public async Task<IReadOnlyList<TotalItemDto>> TotalAsync(MesasContexto ctx, string? estacao, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var (_, _, garcom) = await Base(c, ctx, ct);
        var linhas = await c.QueryAsync<CozinhaLinha>(new CommandDefinition(LinhaSelect + """
             WHERE i.estabelecimento_id = @loja AND i.estado IN ('em_fila', 'em_preparo')
               AND (CAST(@garcom AS uuid) IS NULL OR s.garcom_id = @garcom)
            """, new { loja = ctx.Loja, garcom }, cancellationToken: ct));
        return CozinhaRegras.Total(linhas, estacao);
    }

    /// <summary>O que é do garçom agora. <paramref name="garcomId"/> vazio = quem está logado; gestor pode ver qualquer um.</summary>
    public async Task<GarcomPedidosDto> GarcomAsync(MesasContexto ctx, Guid? garcomId, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var (cfg, agora, meu) = await Base(c, ctx, ct);
        var g = meu ?? garcomId ?? await c.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition("""
            SELECT id FROM gestao_pessoas WHERE estabelecimento_id = @loja AND usuario_id = @u AND ativo LIMIT 1
            """, new { loja = ctx.Loja, u = ctx.Usuario }, cancellationToken: ct));
        var linhas = (await c.QueryAsync<CozinhaLinha>(new CommandDefinition(LinhaSelect + """
             WHERE i.estabelecimento_id = @loja AND s.estado = 'aberta'
               AND i.estado IN ('aguardando_aprovacao', 'em_fila', 'em_preparo', 'pronto', 'aguardando_retirada')
               AND (CAST(@g AS uuid) IS NULL OR s.garcom_id = @g OR i.leva_garcom_id = @g)
            """, new { loja = ctx.Loja, g }, cancellationToken: ct))).ToList();
        ParaLevarDto L(CozinhaLinha i) => new(i.ItemId, i.PedidoId, i.MesaId, i.MesaNumero, i.Nome, i.Foto, i.Qtd, i.LevaGarcomId ?? i.SessaoGarcomId,
            i.AvisadoEm ?? i.TPronto, i.PegouEm, i.PegouNome, i.Estado);
        var prontos = linhas.Where(i => i.Estado == "pronto" && i.QuemLeva != "cliente" && (g is null || (i.LevaGarcomId ?? i.SessaoGarcomId) == g)).ToList();
        var aprovar = linhas.Where(i => i.Estado == "aguardando_aprovacao").GroupBy(i => i.PedidoId).Select(x =>
        {
            var p = x.First();
            return new AprovarDto(x.Key, p.MesaId, p.MesaNumero, p.Origem, p.PedidoCriadoEm, null, x.Select(i => CozinhaRegras.Item(i, x.ToList())).ToList());
        }).OrderBy(a => a.CriadoEm).ToList();
        return new GarcomPedidosDto(g,
            prontos.Where(i => i.PegouEm is null).OrderBy(i => i.AvisadoEm ?? i.TPronto).Select(L).ToList(),
            prontos.Where(i => i.PegouEm is not null).OrderBy(i => i.PegouEm).Select(L).ToList(),
            aprovar,
            linhas.Count(i => i.Estado is "em_fila" or "em_preparo"),
            linhas.Where(i => i.Estado == "aguardando_retirada").Select(L).ToList());
    }

    /// <summary><c>/cozinha/para-levar?garcomId=</c>: só o que o garçom leva (cliente busca fica de fora).</summary>
    public async Task<IReadOnlyList<ParaLevarDto>> ParaLevarAsync(MesasContexto ctx, Guid? garcomId, CancellationToken ct)
    {
        var g = await GarcomAsync(ctx, garcomId, ct);
        return g.ParaLevar.Concat(g.ACaminho).ToList();
    }

    /// <summary>Pedidos das mesas abertas (quadro da aba Mesas em Pedidos, P11). Filtros: mesa e estado do pedido.</summary>
    public async Task<IReadOnlyList<PedidoResumoDto>> PedidosAsync(MesasContexto ctx, Guid? mesaId, string? estado, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var (_, _, garcom) = await Base(c, ctx, ct);
        var linhas = (await c.QueryAsync<CozinhaLinha>(new CommandDefinition(LinhaSelect + """
             WHERE i.estabelecimento_id = @loja AND (s.estado = 'aberta' OR i.criado_em > NOW() - INTERVAL '12 hours')
               AND (CAST(@mesaId AS uuid) IS NULL OR i.mesa_id = @mesaId)
               AND (CAST(@estado AS text) IS NULL OR p.estado = @estado)
               AND (CAST(@garcom AS uuid) IS NULL OR s.garcom_id = @garcom)
             ORDER BY p.criado_em DESC
             LIMIT 600
            """, new { loja = ctx.Loja, mesaId, estado, garcom }, cancellationToken: ct))).ToList();
        var pedidos = linhas.Select(l => l.PedidoId).Distinct().ToArray();
        var info = (await c.QueryAsync<(Guid Id, string Estado, string? AtorNome, DateTime? VistoEm, Guid? EscaladoPara)>(new CommandDefinition(
            "SELECT id, estado, ator_nome, visto_em, escalado_para FROM mesa_pedido WHERE id = ANY(@pedidos)", new { pedidos }, cancellationToken: ct)))
            .ToDictionary(x => x.Id);
        var recados = (await c.QueryAsync<(Guid PedidoId, string Tipo, string? Texto, string? Para, string? Nome, DateTime Quando)>(new CommandDefinition(
            "SELECT pedido_id, tipo, texto, para, por_nome, quando FROM mesa_recado WHERE pedido_id = ANY(@pedidos) ORDER BY quando",
            new { pedidos }, cancellationToken: ct))).ToLookup(r => r.PedidoId);
        return linhas.GroupBy(l => l.PedidoId).Select(g =>
        {
            var p = g.First(); var i = info[g.Key];
            return new PedidoResumoDto(g.Key, p.SessaoId, p.MesaId, p.MesaNumero, p.Origem, i.Estado, i.AtorNome, p.PedidoCriadoEm, p.SegurarPrincipal,
                i.VistoEm, i.EscaladoPara, g.Select(x => CozinhaRegras.Item(x, g.ToList())).ToList(),
                recados[g.Key].Select(r => new RecadoDto(r.Tipo, r.Texto, r.Para, r.Nome, r.Quando)).ToList());
        }).ToList();
    }

    public async Task<IReadOnlyList<AvisoGestorDto>> AvisosAsync(MesasContexto ctx, bool? lidos, CancellationToken ct)
    {
        R.Exigir(ctx.Papel, "GD");
        await using var c = await source.OpenConnectionAsync(ct);
        return (await c.QueryAsync<AvisoGestorDto>(new CommandDefinition("""
            SELECT id, tipo, mesa_id, item_id, valor, motivo, por_nome, texto, lido, criado_em FROM mesa_aviso_gestor
             WHERE estabelecimento_id = @loja AND (CAST(@lidos AS boolean) IS NULL OR lido = @lidos)
             ORDER BY criado_em DESC LIMIT 200
            """, new { loja = ctx.Loja, lidos }, cancellationToken: ct))).ToList();
    }

    /// <summary>Posição do item na fila da estação, para o cliente (P18 expõe em <c>/api/public/mesas/{token}/fila/{itemId}</c>).</summary>
    public async Task<FilaClienteDto?> FilaClienteAsync(Guid loja, Guid itemId, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var it = await c.QueryFirstOrDefaultAsync<(string Estado, string EstacaoId, DateTime? TFila, DateTime CriadoEm)>(new CommandDefinition(
            "SELECT estado, estacao_id, t_fila, criado_em FROM mesa_item WHERE id = @itemId AND estabelecimento_id = @loja", new { itemId, loja }, cancellationToken: ct));
        if (it.Estado is null) return null;
        var aFrente = it.Estado is "em_fila" or "em_preparo" ? await c.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT count(*) FROM mesa_item WHERE estabelecimento_id = @loja AND estacao_id = @e AND estado IN ('em_fila', 'em_preparo')
               AND COALESCE(t_fila, criado_em) < @desde
            """, new { loja, e = it.EstacaoId, desde = it.TFila ?? it.CriadoEm }, cancellationToken: ct)) : 0;
        var dados = await c.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT dados::text FROM mesas_config WHERE estabelecimento_id = @loja",
            new { loja }, cancellationToken: ct));
        var cfg = MesasConfigRegras.Efetiva(dados is null ? null : JsonNode.Parse(dados) as JsonObject);
        var tempo = R.TempoEsperado(cfg, it.EstacaoId);
        var estimado = it.Estado is "em_fila" or "em_preparo" ? tempo + aFrente * Math.Max(1, tempo / 4) : 0;
        return new FilaClienteDto(itemId, it.Estado, aFrente, it.Estado is "em_fila" or "em_preparo" ? CozinhaRegras.Faixa(estimado) : "pronto");
    }

    private static List<EstacaoResumoDto> Estacoes(JsonObject cfg, List<CozinhaLinha> linhas, DateTime agora)
    {
        var lista = new List<EstacaoResumoDto>();
        if (MesasConfigRegras.Ler(cfg, "estacoes") is not JsonObject est) return lista;
        foreach (var (id, _) in est)
        {
            if (R.EstacaoValida(cfg, id) != id) continue;   // desligada ou setor que a loja não tem: some do quadro
            var fazendo = linhas.Where(l => l.EstacaoId == id && l.Estado is "em_fila" or "em_preparo").ToList();
            var esperado = R.TempoEsperado(cfg, id);
            var via = id is "cozinha" or "bar" ? R.Texto(MesasConfigRegras.Ler(cfg, $"preparo.{id}.via")) : null;
            lista.Add(new EstacaoResumoDto(id, fazendo.Count, fazendo.Count(l => R.Atrasado((int)(agora - (l.TFila ?? l.CriadoEm)).TotalMinutes, esperado)),
                R.TemPainel(cfg, id), via, esperado));
        }
        return lista;
    }

    /// <summary>Config efetiva, hora do banco e o garçom (só quando quem lê é garçom: escopo "meu").</summary>
    private static async Task<(JsonObject Cfg, DateTime Agora, Guid? Garcom)> Base(NpgsqlConnection c, MesasContexto ctx, CancellationToken ct)
    {
        var dados = await c.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT dados::text FROM mesas_config WHERE estabelecimento_id = @loja",
            new { loja = ctx.Loja }, cancellationToken: ct));
        var agora = await c.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT NOW()", cancellationToken: ct));
        Guid? garcom = null;
        if (ctx.Papel == MesasPermissoes.PapelGarcom)
            garcom = await c.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition("""
                SELECT id FROM gestao_pessoas WHERE estabelecimento_id = @loja AND usuario_id = @u AND ativo ORDER BY atualizado_em DESC LIMIT 1
                """, new { loja = ctx.Loja, u = ctx.Usuario }, cancellationToken: ct)) ?? Guid.Empty;
        return (MesasConfigRegras.Efetiva(dados is null ? null : JsonNode.Parse(dados) as JsonObject), agora, garcom);
    }
}
