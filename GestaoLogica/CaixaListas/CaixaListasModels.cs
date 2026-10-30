namespace APIBack.GestaoLogica.CaixaListas;

// Listas configuráveis do Caixa por loja (6C2-C). Só rótulos: o movimento e o fechamento gravam o texto escolhido.
// As formas de pagamento (din, pix, deb, cre, ifood) têm significado financeiro e ficam fora destas listas.

/// <summary>Grupos: sang, sup, pag, ret (motivos do movimento de gaveta) e fechamento (justificativa de diferença).</summary>
public static class GrupoLista
{
    public const string Sangria = "sang", Suprimento = "sup", Pagamento = "pag", Devolucao = "ret", Fechamento = "fechamento";
    public static readonly string[] Todos = { Sangria, Suprimento, Pagamento, Devolucao, Fechamento };

    /// <summary>Opções que existem em toda loja desde o início (iguais aos textos fixos da tela). A chave estável identifica o padrão.</summary>
    public static readonly IReadOnlyList<(string Grupo, string Chave, string Nome)> Padroes = new[]
    {
        (Sangria, "cofre", "Cofre"), (Sangria, "deposito", "Depósito no banco"), (Sangria, "gerente", "Pagamento ao gerente"),
        (Suprimento, "troco", "Reforço de troco"), (Suprimento, "cofre", "Veio do cofre"),
        (Pagamento, "gas", "Entregador de gás"), (Pagamento, "hortifruti", "Hortifruti (compra)"), (Pagamento, "diaria", "Diária de colaborador"),
        (Pagamento, "frete", "Frete"),
        (Devolucao, "cancelado", "Pedido cancelado"), (Devolucao, "troco", "Troco errado"), (Devolucao, "faltando", "Item faltando"),
        (Fechamento, "troco", "Troco dado errado"), (Fechamento, "forma", "Pagamento lançado na forma errada"),
        (Fechamento, "devolucao", "Devolução sem registro"), (Fechamento, "nao_sei", "Não sei explicar"),
    };
}

/// <summary><c>Origem</c>: "padrao" ou "personalizada". Padrão renomeado continua "padrao".</summary>
public sealed record OpcaoListaDto(Guid Id, string Grupo, string Nome, string Origem, bool Ativo, int Ordem);

public sealed record CriarOpcaoRequest(string? ChaveIdempotencia, string? Grupo, string? Nome);

public sealed record AtualizarOpcaoRequest(string? Nome, bool? Ativo, int? Ordem);
