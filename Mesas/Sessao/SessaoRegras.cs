using System.Globalization;
using System.Text;

namespace APIBack.Mesas.Sessao;

/// <summary>
/// Regras puras da sessão da mesa (CONTRATO-API §2.1, §2.2, §3.4), sem banco, para testar. Textos iguais aos do protótipo.
/// </summary>
public static class SessaoRegras
{
    public const string Livre = "livre", Reservada = "reservada", Ocupada = "ocupada", EmAtendimento = "em_atendimento",
        AguardandoPagamento = "aguardando_pagamento", AguardandoLimpeza = "aguardando_limpeza", Indisponivel = "indisponivel";

    /// <summary>Status em que a mesa tem sessão aberta (o banco exige <c>sessao_id</c> nesses).</summary>
    public static bool Aberta(string status) => status is Ocupada or EmAtendimento or AguardandoPagamento;

    public static bool PodeAbrir(string status) => status is Livre or Reservada;

    public static string Rotulo(string status) => status switch
    {
        Livre => "livre",
        Reservada => "reservada",
        Ocupada => "ocupada",
        EmAtendimento => "em atendimento",
        AguardandoPagamento => "com a conta pedida",
        AguardandoLimpeza => "esperando limpeza",
        Indisponivel => "fora de uso",
        _ => status
    };

    /// <summary>Peso de cada alerta (o mais urgente primeiro), igual ao protótipo (<c>ZM.dados.ALERTAS</c>).</summary>
    public static readonly IReadOnlyDictionary<string, int> PesoAlerta = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["chamado"] = 5, ["aprovacao_pendente"] = 4, ["atraso_cozinha"] = 4, ["autorizacao"] = 4, ["conta_pedida"] = 3,
        ["pedido_pronto"] = 3, ["pagamento_parcial"] = 2, ["sem_pedido"] = 2, ["rodizio_tempo"] = 2, ["aviso_gestor"] = 2,
        ["tempo_excedido"] = 1, ["reserva_chegando"] = 1
    };

    /// <summary>Um ícone só por mesa no mapa: o do alerta mais urgente que tem ícone.</summary>
    private static readonly IReadOnlyDictionary<string, string> IconeAlerta = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["chamado"] = "chamou", ["pedido_pronto"] = "pronto", ["conta_pedida"] = "conta", ["aprovacao_pendente"] = "aprovar",
        ["atraso_cozinha"] = "atraso", ["autorizacao"] = "autorizar", ["sem_pedido"] = "tempo", ["tempo_excedido"] = "tempo",
        ["rodizio_tempo"] = "tempo", ["reserva_chegando"] = "reserva", ["pagamento_parcial"] = "conta", ["aviso_gestor"] = "aviso"
    };

    public sealed record LimitesAlerta(int TempoMesaMin, int SemPedidoMin);

    public sealed record SituacaoMesa(
        string Status, DateTime? AbertaEm, DateTime? ContaPedidaEm, DateTime? UltimoPedidoEm, bool ChamadoAberto, bool AutorizacaoPendente);

    /// <summary>
    /// Alertas que a sessão sabe calcular (chamado, conta pedida, sem pedido há X, tempo excedido, reserva chegando, autorização).
    /// Os de pedido e dinheiro (pronto, atraso, parcial) chegam pelas fontes do P08/P14 em <paramref name="extras"/>.
    /// Limite 0 desliga o alerta. Volta ordenado pelo peso.
    /// </summary>
    public static IReadOnlyList<string> Alertas(SituacaoMesa m, DateTime agora, LimitesAlerta limites, IEnumerable<string>? extras = null)
    {
        var a = new List<string>();
        if (m.ChamadoAberto) a.Add("chamado");
        if (m.AutorizacaoPendente) a.Add("autorizacao");
        if (Aberta(m.Status) && m.ContaPedidaEm is not null) a.Add("conta_pedida");
        if (Aberta(m.Status) && m.AbertaEm is DateTime aberta)
        {
            if (limites.TempoMesaMin > 0 && (agora - aberta).TotalMinutes > limites.TempoMesaMin) a.Add("tempo_excedido");
            var desde = m.UltimoPedidoEm ?? aberta;
            if (limites.SemPedidoMin > 0 && m.ContaPedidaEm is null && m.Status != AguardandoPagamento
                && (agora - desde).TotalMinutes > limites.SemPedidoMin) a.Add("sem_pedido");
        }
        if (m.Status == Reservada) a.Add("reserva_chegando");
        if (extras is not null) a.AddRange(extras);
        return a.Distinct(StringComparer.Ordinal)
            .Select((x, i) => (x, i))
            .OrderByDescending(t => PesoAlerta.TryGetValue(t.x, out var p) ? p : 0).ThenBy(t => t.i)
            .Select(t => t.x).ToList();
    }

    public static string? Icone(IReadOnlyList<string> alertas) =>
        alertas.Select(a => IconeAlerta.TryGetValue(a, out var i) ? i : null).FirstOrDefault(i => i is not null);

    /// <summary>Minutos inteiros desde a abertura, só pelo relógio do servidor (o defeito "27h" do protótipo vinha do relógio errado).</summary>
    public static int Minutos(DateTime? abertaEm, DateTime agora) =>
        abertaEm is DateTime a && agora > a ? (int)Math.Floor((agora - a).TotalMinutes) : 0;

    /// <summary>Celular em E.164. Aceita "(11) 98888-7777", "11988887777" (Brasil) e "+5511988887777". Null = vazio; inválido lança.</summary>
    public static string? Celular(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var t = texto.Trim();
        var digitos = new string(t.Where(char.IsAsciiDigit).ToArray());
        if (t.StartsWith('+')) return digitos.Length is >= 8 and <= 15 && digitos[0] != '0' ? "+" + digitos : null;
        if (digitos.Length is 10 or 11) return "+55" + digitos;
        if (digitos.Length is 12 or 13 && digitos.StartsWith("55", StringComparison.Ordinal)) return "+" + digitos;
        return null;
    }

    /// <summary>Texto livre: apara, vazio vira null, mais longo que o limite volta erro.</summary>
    public static string? Texto(string? texto, int limite, string campo, out string? erro)
    {
        erro = null;
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var t = texto.Trim();
        if (t.Length > limite) erro = campo + " longo demais.";
        return t;
    }

    public static string PrimeiroNome(string? nome) =>
        string.IsNullOrWhiteSpace(nome) ? "Cliente" : nome.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>Mesmo nome sem acento e sem caixa (o garçom pôs "Júlia" e ela entra pelo QR como "julia": é a mesma pessoa).</summary>
    public static string NomeComparavel(string? nome)
    {
        var d = (nome ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(d.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    public static readonly string[] CoresPessoa =
        { "#0ea5e9", "#f43f5e", "#8b5cf6", "#10b981", "#f59e0b", "#6366f1", "#ec4899", "#14b8a6", "#84cc16", "#ef4444" };

    public static string Cor(int ordem) => CoresPessoa[Math.Abs(ordem) % CoresPessoa.Length];

    /// <summary>
    /// Cadeira na mesa de destino ao transferir: a cadeira é "&lt;chave da mesa&gt;:c&lt;n&gt;" (desenho da planta). Mantém o número
    /// se a mesa nova tiver lugar; senão a pessoa fica sem cadeira.
    /// </summary>
    public static string? CadeiraNaMesa(string? cadeiraId, string chaveDestino, int capacidadeDestino)
    {
        if (string.IsNullOrEmpty(cadeiraId)) return null;
        var i = cadeiraId.LastIndexOf(":c", StringComparison.Ordinal);
        if (i < 0 || !int.TryParse(cadeiraId[(i + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        return n >= 1 && n <= capacidadeDestino ? chaveDestino + ":c" + n : null;
    }

    /// <summary>Autonomia do garçom (§5 <c>autonomiaGarcom.regras</c>): <c>sozinho</c>, <c>motivo</c> ou <c>gerente</c>. Outros papéis passam.</summary>
    public enum Autonomia { Pode, ExigeMotivo, PedeGerente }

    public static Autonomia Decidir(string papel, string? regra, bool temMotivo)
    {
        if (papel != "garcom") return Autonomia.Pode;
        return regra switch
        {
            "gerente" => Autonomia.PedeGerente,
            "motivo" when !temMotivo => Autonomia.ExigeMotivo,
            _ => Autonomia.Pode
        };
    }

    /// <summary>Rótulo da ação na auditoria (mesmos do protótipo); ação desconhecida aparece como veio.</summary>
    public static string RotuloAcao(string acao) => Rotulos.TryGetValue(acao, out var r) ? r : acao;

    private static readonly IReadOnlyDictionary<string, string> Rotulos = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["abrirMesa"] = "Abriu a mesa", ["incluirPessoa"] = "Incluiu cliente", ["sentar"] = "Escolheu cadeira",
        ["trocarAssento"] = "Trocou de cadeira", ["trocarLugares"] = "Trocou lugares", ["marcarAlergia"] = "Anotou alergia",
        ["anotarMesa"] = "Anotou na mesa", ["chamarGarcom"] = "Chamou o garçom", ["caminhoChamado"] = "Foi atender chamado",
        ["passarChamado"] = "Passou chamado", ["atenderChamado"] = "Atendeu chamado", ["pedirConta"] = "Pediu a conta",
        ["transferirMesa"] = "Transferiu", ["juntarMesas"] = "Juntou mesas", ["separarMesas"] = "Separou mesas",
        ["encerrar"] = "Encerrou a mesa", ["reabrir"] = "Reabriu a mesa", ["liberarLimpeza"] = "Liberou a mesa",
        ["atribuirGarcom"] = "Trocou o garçom", ["atenderMesa"] = "Assumiu a mesa", ["pedirAutorizacao"] = "Pediu autorização",
        ["responderAutorizacao"] = "Respondeu autorização", ["bloquearMesa"] = "Bloqueou a mesa", ["desbloquearMesa"] = "Desbloqueou a mesa",
        ["configurar"] = "Mudou configuração", ["publicarPlanta"] = "Publicou a planta", ["salvarRascunhoPlanta"] = "Salvou rascunho da planta",
        ["descartarRascunhoPlanta"] = "Descartou rascunho da planta", ["criarMesa"] = "Criou a mesa", ["renumerarMesa"] = "Renumerou a mesa",
        ["desativarMesa"] = "Tirou a mesa da planta", ["reativarMesa"] = "Voltou a mesa para a planta",
        ["lancarPedido"] = "Lançou pedido", ["entregar"] = "Entregou", ["cancelarItem"] = "Cancelou item", ["pagar"] = "Registrou pagamento",
        ["aplicarDesconto"] = "Deu desconto", ["estornar"] = "Estornou pagamento", ["dividirConta"] = "Dividiu a conta"
    };

    /// <summary>Frase da linha da auditoria: "Ana · Abriu a mesa · Mesa 4 · Livre → 3 pessoas".</summary>
    public static string TextoEvento(string ator, string acao, string? alvo, string? antes, string? depois)
    {
        var sb = new StringBuilder(ator).Append(" · ").Append(RotuloAcao(acao));
        if (!string.IsNullOrWhiteSpace(alvo)) sb.Append(" · ").Append(alvo);
        if (!string.IsNullOrWhiteSpace(antes) && !string.IsNullOrWhiteSpace(depois)) sb.Append(" · ").Append(antes).Append(" → ").Append(depois);
        else if (!string.IsNullOrWhiteSpace(depois)) sb.Append(" · ").Append(depois);
        return sb.ToString();
    }

    /// <summary>"4 pessoas" / "1 pessoa" / null.</summary>
    public static string? Pessoas(int n) => n <= 0 ? null : n == 1 ? "1 pessoa" : n + " pessoas";
}
