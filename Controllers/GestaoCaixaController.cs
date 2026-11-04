using APIBack.Acontecimentos;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.GestaoLogica.Caixa;
using APIBack.DTOs.Delivery;
using APIBack.Security;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers;

public sealed record ConferirContagemRequest(DateOnly Dia, ContagemCaixa Contagem);

/// <summary>
/// Caixa do dia (CONTRATOS §9.7), espelhando a interface CaixaFonte do Painel. Gerente e operador de caixa; o operador só vê e
/// registra hoje. Fechamentos do mês e correção do contado são do gerente.
/// </summary>
[ApiController]
[Route("api/gestao/caixa")]
public sealed class GestaoCaixaController(ICaixaService service, IPedidoCoreService pedidos) : ControllerBase
{
    [HttpGet("dias/{dia}")]
    public Task<IActionResult> Dia(DateOnly dia, CancellationToken ct) => Run(false, async (loja, _, acesso) =>
    {
        await SoHojeParaOperador(loja, acesso, dia, ct);
        return await service.CarregarDia(loja, dia, ct);
    });

    /// <summary>Que dia o caixa está rodando (dia em operação, virada, turno) e os fechamentos que impedem abrir outro.</summary>
    [HttpGet("operacao")]
    public Task<IActionResult> Operacao(CancellationToken ct) => Run(false, async (loja, _, _) => await service.Operacao(loja, ct));

    [HttpPost("abrir")]
    public Task<IActionResult> Abrir([FromBody] AbrirCaixaRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Abrir(loja, usuario, acesso, req ?? throw Vazio(), ct));

    /// <summary>Caixa de hoje fechado: abre outro turno (o fechamento vale) com o fundo informado.</summary>
    [HttpPost("turno")]
    public Task<IActionResult> NovoTurno([FromBody] NovoTurnoRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.NovoTurno(loja, usuario, acesso, req ?? throw Vazio(), ct));

    /// <summary>Caixa de hoje fechado: desfaz o fechamento (estornos no livro) e o caixa continua aberto. Motivo obrigatório.</summary>
    [HttpPost("reabrir")]
    public Task<IActionResult> Reabrir([FromBody] ReabrirCaixaRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Reabrir(loja, usuario, acesso, req ?? throw Vazio(), ct));

    [HttpPost("movimentos")]
    public Task<IActionResult> Movimento([FromBody] MovimentoCaixaRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.RegistrarMovimento(loja, usuario, acesso, req ?? throw Vazio(), ct));

    [HttpPost("contagem")]
    public Task<IActionResult> Contagem([FromBody] ConferirContagemRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
    {
        if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode contar o caixa.", 403);
        var r = req ?? throw Vazio();
        return await service.ConferirContagem(loja, usuario, r.Dia, r.Contagem, ct);
    });

    [HttpPost("fechar")]
    public Task<IActionResult> Fechar([FromBody] FecharCaixaRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Fechar(loja, usuario, acesso, req ?? throw Vazio(), ct));

    /// <summary>Configuração do caixa da loja (tolerância do fechamento, operador vê o saldo). Gerente e operador leem; só o gerente muda.</summary>
    [HttpGet("config")]
    public Task<IActionResult> Config(CancellationToken ct) => Run(false, async (loja, _, _) => await service.Config(loja, ct));

    [HttpPut("config")]
    public Task<IActionResult> SalvarConfig([FromBody] SalvarConfigDoCaixaRequest? req, CancellationToken ct) => Run(true, async (loja, usuario, _) =>
        await service.SalvarConfig(loja, usuario, req ?? throw Vazio(), ct));

    /// <summary>Fechamentos enviados pelo operador esperando o gerente (o aviso do painel).</summary>
    [HttpGet("aprovacoes")]
    public Task<IActionResult> Aprovacoes(CancellationToken ct) => Run(true, async (loja, _, _) => await service.Aguardando(loja, ct));

    /// <summary>O gerente aprova (com recontagem opcional) ou devolve o fechamento enviado.</summary>
    [HttpPost("fechamentos/{dia}/decisao")]
    public Task<IActionResult> DecidirFechamento(DateOnly dia, [FromBody] DecisaoDoFechamentoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario, acesso) =>
        await service.DecidirFechamento(loja, usuario, acesso, dia, req ?? throw Vazio(), ct));

    /// <summary>O que dá para pagar em dinheiro pela gaveta (diárias e compras a pagar). Gerente e operador.</summary>
    [HttpGet("a-pagar")]
    public Task<IActionResult> APagar(CancellationToken ct) => Run(false, async (loja, _, _) => await service.APagar(loja, ct));

    [HttpGet("fechamentos")]
    public Task<IActionResult> Fechamentos([FromQuery] DateOnly mes, CancellationToken ct) => Run(true, async (loja, _, _) =>
        await service.Fechamentos(loja, mes, ct));

    [HttpPost("fechamentos/{dia}/ajustes")]
    public Task<IActionResult> Ajustar(DateOnly dia, [FromBody] AjustarFechamentoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario, acesso) =>
    {
        GestaoPapeis.ExigirCorrecao(acesso);
        return await service.AjustarFechamento(loja, usuario, dia, req ?? throw Vazio(), ct);
    });

    // ---- 6C2-B: correções (movimento errado e fundo de troco). O operador pede; o gerente estorna, aprova ou rejeita.

    [HttpPost("movimentos/{id}/solicitacoes")]
    public Task<IActionResult> Solicitar(string id, [FromBody] SolicitarCorrecaoRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Solicitar(loja, usuario, acesso, MovimentoId(id), req ?? throw Vazio(), ct));

    [HttpPost("movimentos/{id}/estorno")]
    public Task<IActionResult> Estornar(string id, [FromBody] EstornarMovimentoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario, acesso) =>
        await service.EstornarMovimento(loja, usuario, acesso, MovimentoId(id), req ?? throw Vazio(), ct));

    [HttpGet("solicitacoes")]
    public Task<IActionResult> Solicitacoes([FromQuery] string? status, [FromQuery] DateOnly? dia, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Solicitacoes(loja, usuario, acesso, status, dia, ct));

    [HttpPost("solicitacoes/{id:guid}/decisao")]
    public Task<IActionResult> Decisao(Guid id, [FromBody] DecisaoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario, acesso) =>
        await service.Decidir(loja, usuario, acesso, id, req ?? throw Vazio(), ct));

    /// <summary>Gerente corrige na hora (200). Operador só cria o pedido (202), sem mexer em dinheiro.</summary>
    [HttpPost("dias/{dia}/corrigir-fundo")]
    public Task<IActionResult> CorrigirFundo(DateOnly dia, [FromBody] CorrigirFundoRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.CorrigirFundo(loja, usuario, acesso, dia, req ?? throw Vazio(), ct));

    // ---- 6C5: aba Motoboys (acerto em lote) e recebimento do que ficou a receber. Gerente e operador; correção de lote só o gerente.

    /// <summary>Motoboys com entregas pendentes, em rota ou acertados no dia, com os lotes do dia.</summary>
    [HttpGet("dias/{dia}/motoboys")]
    public Task<IActionResult> Motoboys(DateOnly dia, CancellationToken ct) => Run(false, async (loja, _, acesso) =>
    {
        await SoHojeParaOperador(loja, acesso, dia, ct);
        return await service.MotoboysDoDia(loja, dia, ct);
    });

    /// <summary>Confirma o lote: pedidos não enviados continuam pendentes (parcial). Pedido já acertado → 409 ACERTO_DESATUALIZADO.</summary>
    [HttpPost("acertos")]
    public Task<IActionResult> Acertar([FromBody] AcertoRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Acertar(loja, usuario, acesso, req ?? throw Vazio(), ct));

    [HttpGet("acertos")]
    public Task<IActionResult> Acertos([FromQuery] DateOnly dia, CancellationToken ct) => Run(false, async (loja, _, acesso) =>
    {
        await SoHojeParaOperador(loja, acesso, dia, ct);
        return await service.Acertos(loja, dia, ct);
    });

    /// <summary>Situação do acerto de um motoboy (a 6C6 lê para o app do motoboy).</summary>
    [HttpGet("acertos/situacao")]
    public Task<IActionResult> SituacaoAcerto([FromQuery] int motoboyId, CancellationToken ct) => Run(false, async (loja, _, _) =>
        await service.SituacaoDoMotoboy(loja, motoboyId, ct));

    [HttpPost("acertos/{id:guid}/correcao")]
    public Task<IActionResult> CorrigirAcerto(Guid id, [FromBody] CorrigirAcertoRequest? req, CancellationToken ct) => Run(true, async (loja, usuario, acesso) =>
        await service.CorrigirAcerto(loja, usuario, acesso, id, req ?? throw Vazio(), ct));

    /// <summary>Pedidos concluídos cuja venda ficou a receber (sem motoboy e sem iFood).</summary>
    [HttpGet("a-receber")]
    public Task<IActionResult> AReceber(CancellationToken ct) => Run(false, async (loja, _, _) => await service.AReceberNoCaixa(loja, ct));

    [HttpPost("recebimentos")]
    public Task<IActionResult> Receber([FromBody] RecebimentoRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.Receber(loja, usuario, acesso, req ?? throw Vazio(), ct));

    /// <summary>
    /// Recebe antes de entregar: registra o pagamento de um pedido ainda em operação sem concluí-lo (receber não é entregar). Pedido já
    /// pago, com motoboy, do iFood ou entregue → 409.
    /// </summary>
    [HttpPost("recebimentos-antecipados")]
    public Task<IActionResult> ReceberAntecipado([FromBody] RecebimentoAntecipadoRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
        await service.ReceberAntecipado(loja, usuario, acesso, req ?? throw Vazio(), ct));

    /// <summary>
    /// Venda de balcão (decisão do dono, 01/11): vira pedido sem endereço com itens do cardápio, entregue e pago na hora; a venda vai para o
    /// livro pela conclusão do balcão (dinheiro na gaveta, Pix e cartão na maquininha). Exige o caixa de hoje aberto.
    /// </summary>
    [HttpPost("balcao")]
    public Task<IActionResult> Balcao([FromBody] VendaBalcaoRequest? req, CancellationToken ct) => Run(false, async (loja, usuario, acesso) =>
    {
        var r = req ?? throw Vazio();
        if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode registrar no caixa.", 403);
        // Balcão é retirada. Entrega por motoboy tem endereço, taxa e zona: nasce pelo pedido do Delivery, não por aqui.
        if (APIBack.Model.Delivery.PedidoModalidade.Normalize(r.Modalidade) == APIBack.Model.Delivery.PedidoModalidade.Delivery)
            throw new GestaoException("CAMPO_INVALIDO", "Pedido para entrega leva endereço: crie pelo pedido de entrega.", 422);
        var chave = BalcaoRegras.Chave(r.ChaveIdempotencia);
        var concluir = r.Concluir ?? true;
        // Seguindo o fluxo, a forma pode ficar para a retirada: a venda fica a receber no Caixa quando o pedido for concluído.
        var pagaDepois = !concluir && string.IsNullOrWhiteSpace(r.Forma);
        var tipoPagamento = pagaDepois ? null : BalcaoRegras.TipoPagamento(r.Forma);
        var recebido = pagaDepois ? null : BalcaoRegras.ValorRecebido(r.Forma!, r.ValorRecebido);
        var hoje = await service.Hoje(loja, ct);
        if (r.Dia != hoje) throw new GestaoException("CAMPO_INVALIDO", "O caixa registra só o dia de hoje.", 422);
        var diaAtual = await service.CarregarDia(loja, hoje, ct);
        if (diaAtual?.Aguardando is not null)
            throw new GestaoException("AGUARDANDO_APROVACAO", "O fechamento do caixa espera a aprovação do gerente: não dá para vender agora.", 409);
        if (diaAtual is not { Aberto: true })
            throw new GestaoException("SEM_CAIXA", "Abra o caixa antes de vender no balcão.", 409);
        CreatedPedidoDto pedido;
        try
        {
            pedido = await pedidos.CreateBalcaoAsync(loja, usuario,
                new PedidoBalcao(chave, r.Itens ?? new(), tipoPagamento, recebido, r.Cliente, r.Telefone, r.Observacao, concluir));
        }
        catch (DeliveryDomainException ex) { throw new GestaoException(ex.Code, ex.Message, ex.StatusCode); }
        var total = pedido.Total ?? 0m;
        return new VendaBalcaoResposta(pedido.Id, total, recebido is { } v ? CaixaRegras.R2(v - total) : 0m, pedido.JaExistia,
            (await service.CarregarDia(loja, hoje, ct))!, pedido.Status);
    });

    /// <summary>O id da fita é "{acontecimento}" ou "{acontecimento}:{linha}" / "{acontecimento}:q:{liquidação}": vale o primeiro GUID.</summary>
    private static Guid MovimentoId(string id) =>
        Guid.TryParse(id.Split(':')[0], out var guid) ? guid : throw new GestaoException("CAMPO_INVALIDO", "Movimento inválido.", 422);

    private async Task SoHojeParaOperador(Guid loja, GestaoPapeis.Acesso acesso, DateOnly dia, CancellationToken ct)
    {
        if (acesso.Papel == GestaoPapeis.OperadorCaixa && dia != await service.Hoje(loja, ct))
            throw new GestaoException("SEM_PERMISSAO", "O operador de caixa vê só o caixa de hoje.", 403);
    }

    private static GestaoException Vazio() => new("CAMPO_INVALIDO", "Envie os dados.", 422);

    private async Task<IActionResult> Run(bool soGerente, Func<Guid, int, GestaoPapeis.Acesso, Task<object?>> acao)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario) return Unauthorized();
        var acesso = GestaoPapeis.Resolver(HttpContext.TemPermissao, HttpContext.IsSuperAdmin());
        try
        {
            if (acesso.Papel is null || (soGerente && acesso.Papel != GestaoPapeis.Gerente))
                throw new GestaoException("SEM_PERMISSAO", soGerente ? "Só o gerente." : "Você não tem acesso ao caixa.", 403);
            var resultado = await acao(loja, usuario, acesso);
            return resultado is CorrigirFundoResposta { Efetivada: false } ? StatusCode(202, ApiResponse<object?>.Ok(resultado)) : Ok(ApiResponse<object?>.Ok(resultado));
        }
        catch (GestaoException ex) { return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo)); }
    }
}
