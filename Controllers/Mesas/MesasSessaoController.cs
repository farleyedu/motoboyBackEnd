using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Sessao;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Controllers.Mesas;

/// <summary>
/// Sessão da mesa (CONTRATO-API §3.4) e leituras do salão e da mesa (§4.1). Comandos: <c>Mesas.operar</c> (garçom com ponto, gerente, dono;
/// o caixa só encerra); trocar o garçom por escolha do gestor e reabrir: <c>Mesas.gerir</c>. Leituras: <c>Mesas.ver</c>.
/// </summary>
[Route("api/v2/mesas")]
public sealed class MesasSessaoController(IMesasNucleoService nucleo, ISessaoService sessao, ISalaoService salao) : MesasControllerBase(nucleo)
{
    private CancellationToken Ct => HttpContext.RequestAborted;

    [HttpGet("salao")]
    public Task<IActionResult> Salao([FromQuery] string? pisoId) => Run(MesasPermissoes.Ver, async ctx => await salao.SalaoAsync(ctx.Loja, pisoId, Ct));

    [HttpGet("sessoes/mesa/{mesaId:guid}")]
    public Task<IActionResult> Mesa(Guid mesaId) => Run(MesasPermissoes.Ver, async ctx => await salao.MesaAsync(ctx.Loja, mesaId, Ct));

    [HttpGet("sessoes/{sessaoId:guid}/relatorio")]
    public Task<IActionResult> Relatorio(Guid sessaoId) => Run(MesasPermissoes.Ver, async ctx => await salao.RelatorioAsync(ctx.Loja, sessaoId, Ct));

    [HttpPost("sessoes/abrir-mesa")]
    public Task<IActionResult> AbrirMesa([FromBody] AbrirMesaRequest? req) =>
        Operar(ctx => sessao.AbrirMesaAsync(ctx, req ?? new(null, null, null, null, null, null, null, null, null), Ct));

    [HttpPost("sessoes/incluir-pessoa")]
    public Task<IActionResult> IncluirPessoa([FromBody] IncluirPessoaRequest? req) =>
        Operar(ctx => sessao.IncluirPessoaAsync(ctx, req ?? new(null, null, null, null, null, null, null, null), Ct));

    [HttpPost("sessoes/sentar")]
    public Task<IActionResult> Sentar([FromBody] SentarRequest? req) => Operar(ctx => sessao.SentarAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/trocar-assento")]
    public Task<IActionResult> TrocarAssento([FromBody] SentarRequest? req) => Operar(ctx => sessao.SentarAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/trocar-lugares")]
    public Task<IActionResult> TrocarLugares([FromBody] TrocarLugaresRequest? req) =>
        Operar(ctx => sessao.TrocarLugaresAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/marcar-alergia")]
    public Task<IActionResult> MarcarAlergia([FromBody] MarcarAlergiaRequest? req) =>
        Operar(ctx => sessao.MarcarAlergiaAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/anotar-mesa")]
    public Task<IActionResult> AnotarMesa([FromBody] AnotarMesaRequest? req) =>
        Operar(ctx => sessao.AnotarMesaAsync(ctx, req ?? new(null, null, null, null), Ct));

    [HttpPost("sessoes/atender-mesa")]
    public Task<IActionResult> AtenderMesa([FromBody] GarcomMesaRequest? req) =>
        Operar(ctx => sessao.TrocarGarcomAsync(ctx, req ?? new(null, null, null, null), true, Ct));

    [HttpPost("sessoes/atribuir-garcom")]
    public Task<IActionResult> AtribuirGarcom([FromBody] GarcomMesaRequest? req) =>
        Run(MesasPermissoes.Gerir, async ctx => await sessao.TrocarGarcomAsync(ctx, req ?? new(null, null, null, null), false, Ct));

    [HttpPost("sessoes/transferir-mesa")]
    public Task<IActionResult> TransferirMesa([FromBody] TransferirMesaRequest? req) =>
        Operar(ctx => sessao.TransferirMesaAsync(ctx, req ?? new(null, null, null, null, null, null), Ct));

    [HttpPost("sessoes/juntar-mesas")]
    public Task<IActionResult> JuntarMesas([FromBody] JuntarMesasRequest? req) =>
        Operar(ctx => sessao.JuntarMesasAsync(ctx, req ?? new(null, null, null, null), Ct));

    [HttpPost("sessoes/separar-mesas")]
    public Task<IActionResult> SepararMesas([FromBody] MesaRequest? req) => Operar(ctx => sessao.SepararMesasAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/pedir-conta")]
    public Task<IActionResult> PedirConta([FromBody] MesaRequest? req) => Operar(ctx => sessao.PedirContaAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/encerrar")]
    public Task<IActionResult> Encerrar([FromBody] EncerrarRequest? req) =>
        Operar(ctx => sessao.EncerrarAsync(ctx, req ?? new(null, null, null, null, null), Ct), caixaPode: true);

    [HttpPost("sessoes/reabrir")]
    public Task<IActionResult> Reabrir([FromBody] ReabrirRequest? req) =>
        Run(MesasPermissoes.Gerir, async ctx => await sessao.ReabrirAsync(ctx, req ?? new(null, null, null, null), Ct));

    [HttpPost("sessoes/liberar-limpeza")]
    public Task<IActionResult> LiberarLimpeza([FromBody] MesaRequest? req) =>
        Operar(ctx => sessao.LiberarLimpezaAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/chamar-garcom")]
    public Task<IActionResult> ChamarGarcom([FromBody] ChamarGarcomRequest? req) =>
        Operar(ctx => sessao.ChamarGarcomAsync(ctx, req ?? new(null, null, null, null, null), Ct));

    [HttpPost("sessoes/caminho-chamado")]
    public Task<IActionResult> CaminhoChamado([FromBody] ChamadoRequest? req) =>
        Operar(ctx => sessao.CaminhoChamadoAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/passar-chamado")]
    public Task<IActionResult> PassarChamado([FromBody] ChamadoRequest? req) =>
        Operar(ctx => sessao.PassarChamadoAsync(ctx, req ?? new(null, null, null), Ct));

    [HttpPost("sessoes/atender-chamado")]
    public Task<IActionResult> AtenderChamado([FromBody] AtenderChamadoRequest? req) =>
        Operar(ctx => sessao.AtenderChamadoAsync(ctx, req ?? new(null, null, null), Ct));

    /// <summary><c>Mesas.operar</c>; o caixa (que também tem operar, para a conta) só entra onde <paramref name="caixaPode"/>.</summary>
    private Task<IActionResult> Operar<T>(Func<MesasContexto, Task<T>> executar, bool caixaPode = false) where T : notnull =>
        Run(MesasPermissoes.Operar, async ctx =>
        {
            if (!caixaPode && ctx.Papel == MesasPermissoes.PapelCaixa) throw MesasException.SemPermissao();
            return await executar(ctx);
        });
}
