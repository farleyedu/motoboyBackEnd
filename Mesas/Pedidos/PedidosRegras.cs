using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;

namespace APIBack.Mesas.Pedidos;

/// <summary>Uma etapa do preparo (estação + nome). Sem etapas configuradas o item tem uma só: "Preparo".</summary>
public sealed class Etapa
{
    public string EstacaoId { get; set; } = "producao_geral";
    public string Nome { get; set; } = "Preparo";
    public bool Feita { get; set; }
}

/// <summary>O que as regras do item mexem (estado, etapa e quem leva). O serviço carrega da linha e grava de volta.</summary>
public sealed class ItemPasso
{
    public string Estado { get; set; } = "rascunho";
    public short EtapaAtual { get; set; }
    public List<Etapa> Etapas { get; set; } = new();
    public string EstacaoId { get; set; } = "producao_geral";
    public string? QuemLeva { get; set; }
    public string? RetiradaEstacao { get; set; }
    public string EstacaoFinal => Etapas.Count > 0 ? Etapas[^1].EstacaoId : EstacaoId;
}

/// <summary>
/// Regras puras do P08 (protótipo: <c>rotear</c>, <c>regraAprovacao</c>, <c>p7gDecide</c>, <c>p7gPassoPronto</c>, <c>desfazerPasso</c>,
/// <c>pedidosDaCozinha</c>). Sem banco: o serviço chama e os testes cobrem.
/// </summary>
public static class PedidosRegras
{
    public const string ProducaoGeral = "producao_geral";
    public static readonly string[] Ativos = { "em_fila", "em_preparo", "pronto", "aguardando_retirada" };
    public static readonly string[] AntesDaCozinha = { "rascunho", "aguardando_aprovacao", "em_fila" };

    // ---------- papéis (letras do contrato: D dono, G gerente, X caixa, W garçom, K cozinha) ----------

    public static bool Pode(string? papel, string letras) => papel switch
    {
        MesasPermissoes.PapelDono => letras.Contains('D'),
        MesasPermissoes.PapelGerente => letras.Contains('G'),
        MesasPermissoes.PapelCaixa => letras.Contains('X'),
        MesasPermissoes.PapelGarcom => letras.Contains('W'),
        MesasPermissoes.PapelCozinha => letras.Contains('K'),
        "sistema" => true,
        _ => false
    };

    public static void Exigir(string? papel, string letras)
    {
        if (!Pode(papel, letras)) throw MesasException.SemPermissao();
    }

    // ---------- roteamento ----------

    /// <summary>Estação desligada (<c>rotas.inativas</c>) ou setor que a loja não tem (<c>preparo.cozinha|bar.existe=false</c>) cai na Produção geral.</summary>
    public static string EstacaoValida(JsonObject cfg, string? estacao)
    {
        var e = string.IsNullOrWhiteSpace(estacao) ? ProducaoGeral : estacao.Trim();
        if (Bool(cfg, "rotas.inativas." + e)) return ProducaoGeral;
        if (e is "cozinha" or "bar" && MesasConfigRegras.Ler(cfg, $"preparo.{e}.existe") is JsonValue v && v.TryGetValue<bool>(out var existe) && !existe)
            return ProducaoGeral;
        return e;
    }

    /// <summary>Ordem: etapas do produto · estação do produto · estação da categoria · Produção geral.</summary>
    public static List<Etapa> Rotear(JsonObject cfg, Guid produtoId, Guid? categoriaId)
    {
        var p = produtoId.ToString();
        if (MesasConfigRegras.Ler(cfg, "rotas.etapas." + p) is JsonArray etapas && etapas.Count > 0)
            return etapas.OfType<JsonObject>().Select(e => new Etapa
            {
                EstacaoId = EstacaoValida(cfg, Texto(e["estacaoId"])),
                Nome = Texto(e["nome"]) is { Length: > 0 } n ? n : "Preparo"
            }).ToList();
        var estacao = Texto(MesasConfigRegras.Ler(cfg, "rotas.produtos." + p))
            ?? (categoriaId is Guid c ? Texto(MesasConfigRegras.Ler(cfg, "rotas.categorias." + c)) : null);
        return new List<Etapa> { new() { EstacaoId = EstacaoValida(cfg, estacao) } };
    }

    /// <summary>Setor com painel? Sem painel o pedido chega pela impressora ou pelo garçom (<c>via</c>); o fluxo do item não muda.</summary>
    public static bool TemPainel(JsonObject cfg, string estacao) =>
        estacao is not ("cozinha" or "bar") || !(MesasConfigRegras.Ler(cfg, $"preparo.{estacao}.painel") is JsonValue v && v.TryGetValue<bool>(out var p) && !p);

    public static int TempoEsperado(JsonObject cfg, string estacao) =>
        MesasConfigRegras.Ler(cfg, "estacoes." + estacao) is JsonValue v && v.TryGetValue<double>(out var d) && d > 0 ? (int)d : 12;

    /// <summary>Entrada e principal (segurar o principal) pelo nome da categoria do cardápio.</summary>
    public static string TipoPrato(string? categoriaSlug)
    {
        var s = SemAcento(categoriaSlug ?? "");
        if (s.Contains("entrada") || s.Contains("petisco") || s.Contains("aperitivo")) return "entrada";
        if (s.Contains("principa") || s.Contains("grelhad") || s.Contains("pizza") || s.Contains("prato") || s.Contains("massa")) return "principal";
        return "outro";
    }

    // ---------- aprovação ----------

    public sealed record Aprovacao(bool Exige, string Rotulo, string? RegraId = null);

    /// <summary>Protótipo <c>regraAprovacao</c>: o que a equipe lança já está aprovado; o do cliente segue o modo; em "regras" a primeira que casa decide.</summary>
    public static Aprovacao RegraAprovacao(JsonObject cfg, string origem, string estacao, Guid? categoriaId, Guid produtoId, decimal preco)
    {
        var modo = Texto(MesasConfigRegras.Ler(cfg, "aprovacao.modo")) ?? "regras";
        if (modo == "direto") return new(false, "Tudo vai direto para a produção");
        if (origem != "cliente") return new(false, "Lançado pela equipe: vai direto");
        if (modo == "garcom") return new(true, "Pedido do cliente passa pelo garçom");
        if (MesasConfigRegras.Ler(cfg, "aprovacao.regras") is JsonArray regras)
            foreach (var r in regras.OfType<JsonObject>())
            {
                var q = r["quando"] as JsonObject ?? new JsonObject();
                var ok = true;
                if (Texto(q["origem"]) is { } o && o != origem) ok = false;
                if (Texto(q["estacaoId"]) is { } e && e != estacao) ok = false;
                if (Texto(q["catId"]) is { } c && c != categoriaId?.ToString()) ok = false;
                if (Texto(q["produtoId"]) is { } p && p != produtoId.ToString()) ok = false;
                if (q["precoAcima"] is JsonValue pa && pa.TryGetValue<double>(out var lim) && !(preco > (decimal)lim)) ok = false;
                if (ok) return new(r["exige"] is JsonValue ex && ex.TryGetValue<bool>(out var b) && b, Texto(r["rotulo"]) ?? "Regra da casa", Texto(r["id"]));
            }
        return new(false, "Sem regra: vai direto");
    }

    // ---------- quem leva ----------

    /// <summary>Protótipo <c>p7gDecide</c>. Modo <c>garcom</c> | <c>cliente</c> (o P01 grava <c>balcao</c>) | <c>depende</c>; config antiga
    /// (<c>ativa</c> + true/false) vira "depende". Exceção do produto vence a da estação.</summary>
    public static string QuemLeva(JsonObject cfg, Guid produtoId, string estacaoFinal, bool querBuscar)
    {
        var r = MesasConfigRegras.Ler(cfg, "retirada") as JsonObject ?? new JsonObject();
        var modo = Texto(r["modo"]) ?? (r["ativa"] is JsonValue a && a.TryGetValue<bool>(out var at) && at ? "depende" : "garcom");
        if (querBuscar && r["clienteEscolhe"] is JsonValue ce && ce.TryGetValue<bool>(out var pode) && pode) return "cliente";
        if (modo is "cliente" or "balcao") return "cliente";
        if (modo != "depende") return "garcom";
        return Norm((r["produtos"] as JsonObject)?[produtoId.ToString()]) ?? Norm((r["estacoes"] as JsonObject)?[estacaoFinal]) ?? "garcom";
    }

    private static string? Norm(JsonNode? v) => v is JsonValue j
        ? j.TryGetValue<bool>(out var b) ? (b ? "cliente" : "garcom")
        : j.TryGetValue<string>(out var s) ? s is "cliente" or "balcao" ? "cliente" : s == "garcom" ? "garcom" : null : null
        : null;

    // ---------- autonomia (chaves do §5) ----------

    public static string ChaveEditar(string estado) => AntesDaCozinha.Contains(estado) ? "editarAntes" : "itemDepois";
    public static string ChaveCancelar(string estado) => AntesDaCozinha.Contains(estado) ? "cancelarAntes" : "itemDepois";
    public static decimal LimiteCortesia(JsonObject cfg) =>
        MesasConfigRegras.Ler(cfg, "autonomiaGarcom.limiteCortesia") is JsonValue v && v.TryGetValue<double>(out var d) ? (decimal)d : 0m;
    /// <summary>Até o limite o garçom dá sozinho; acima vale a regra "cortesia" (padrão: pede o gerente).</summary>
    public static bool CortesiaSozinho(JsonObject cfg, decimal valor) => valor <= LimiteCortesia(cfg) && valor >= 0 && LimiteCortesia(cfg) > 0;

    // ---------- passos do item ----------

    /// <summary>
    /// Termina a etapa da estação atual. Na última vira <c>pronto</c> (garçom leva) ou <c>aguardando_retirada</c> (cliente busca).
    /// Com mais etapas segue <c>em_fila</c> na próxima estação (igual ao protótipo). Devolve o texto do passo.
    /// </summary>
    public static string PassoPronto(ItemPasso it, string quemLeva)
    {
        if (it.Estado is not ("em_fila" or "em_preparo")) throw new InvalidOperationException("estado");
        if (it.Etapas.Count == 0) it.Etapas.Add(new Etapa { EstacaoId = it.EstacaoId });
        it.Etapas[it.EtapaAtual].Feita = true;
        if (it.EtapaAtual < it.Etapas.Count - 1)
        {
            it.EtapaAtual++;
            it.EstacaoId = it.Etapas[it.EtapaAtual].EstacaoId;
            it.Estado = "em_fila";
            return "Seguiu para a próxima estação";
        }
        it.QuemLeva ??= quemLeva;
        if (it.QuemLeva == "cliente")
        {
            it.Estado = "aguardando_retirada";
            it.RetiradaEstacao = it.EstacaoFinal;
            return "Esperando o cliente buscar";
        }
        it.Estado = "pronto";
        return "Pronto";
    }

    /// <summary><c>marcarPronto</c>: todas as etapas feitas de uma vez.</summary>
    public static void TudoPronto(ItemPasso it, string quemLeva)
    {
        if (it.Etapas.Count == 0) it.Etapas.Add(new Etapa { EstacaoId = it.EstacaoId });
        it.EtapaAtual = (short)(it.Etapas.Count - 1);
        it.EstacaoId = it.Etapas[^1].EstacaoId;
        for (var i = 0; i < it.Etapas.Count - 1; i++) it.Etapas[i].Feita = true;
        it.Estado = "em_preparo";
        PassoPronto(it, quemLeva);
    }

    /// <summary>Volta a um estado gravado no histórico (<c>desfazerPasso</c>): estado e etapa de antes do passo.</summary>
    public static void Reverter(ItemPasso it, string de, short? etapaDe)
    {
        var etapa = etapaDe ?? it.EtapaAtual;
        if (it.Etapas.Count > 0)
        {
            etapa = (short)Math.Clamp((int)etapa, 0, it.Etapas.Count - 1);
            for (var i = etapa; i < it.Etapas.Count; i++) it.Etapas[i].Feita = false;
            it.EstacaoId = it.Etapas[etapa].EstacaoId;
        }
        it.EtapaAtual = etapa;
        it.Estado = de;
        if (de != "aguardando_retirada") it.RetiradaEstacao = null;
    }

    /// <summary>Comandos que o "Voltar o último" desfaz (passos de cozinha e entrega).</summary>
    public static readonly string[] Desfazíveis = { "avancarEtapa", "marcarItemPronto", "marcarPronto", "entregar", "marcarRetirado" };

    // ---------- dinheiro (só a divisão do item; a conta é do P14) ----------

    /// <summary>Divide o item igual entre as comandas: percentuais somam 100 e valores somam o total (a sobra do centavo vai na primeira).</summary>
    public static IReadOnlyList<(decimal Percentual, decimal Valor)> Repartir(decimal total, int partes)
    {
        if (partes <= 0) throw new ArgumentOutOfRangeException(nameof(partes));
        var pct = Math.Round(100m / partes, 2, MidpointRounding.AwayFromZero);
        var val = Math.Round(total / partes, 2, MidpointRounding.AwayFromZero);
        var lista = Enumerable.Range(0, partes).Select(_ => (pct, val)).ToList();
        lista[0] = (100m - pct * (partes - 1), Math.Round(total, 2, MidpointRounding.AwayFromZero) - val * (partes - 1));
        return lista;
    }

    // ---------- tempo ----------

    /// <summary>Verde até 1× o tempo do prato, amarelo até 1,5×, vermelho acima.</summary>
    public static string Cor(int minutos, int esperado)
    {
        var r = minutos / (double)Math.Max(1, esperado);
        return r <= 1 ? "verde" : r <= 1.5 ? "amarelo" : "vermelho";
    }

    public static bool Atrasado(int minutos, int esperado) => Cor(minutos, esperado) == "vermelho";

    // ---------- auxiliares ----------

    public static string? Texto(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static bool Bool(JsonObject cfg, string chave) => MesasConfigRegras.Ler(cfg, chave) is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static string SemAcento(string s)
    {
        var n = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (var ch in n) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }

    public static string Qtd(decimal q) => q == Math.Truncate(q) ? ((int)q).ToString(CultureInfo.InvariantCulture) : q.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));
}
