using APIBack.Attributes;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace APIBack.Mesas.Nucleo;

/// <summary>Quem está chamando, já resolvido: loja, usuário e papel no salão.</summary>
public sealed record MesasContexto(Guid Loja, int Usuario, string Papel);

/// <summary>
/// Base dos controllers de <c>/api/v2/mesas</c>. O módulo comercial é exigido aqui (<c>MODULO_INATIVO</c>), o papel vem de
/// <see cref="MesasPermissoes"/> (<c>SEM_PERMISSAO</c>) e o interruptor da loja (<c>mesas_config.ativo</c>) responde
/// <c>MESAS_DESLIGADO</c>, menos onde a ação liberar para dono e gerente (ler e mudar a configuração).
/// </summary>
[ApiController]
[RequireModule(MesasPermissoes.Modulo)]
public abstract class MesasControllerBase(IMesasNucleoService nucleo) : ControllerBase
{
    protected IMesasNucleoService Nucleo => nucleo;

    protected async Task<IActionResult> Run(string acao, Func<MesasContexto, Task<object>> executar, bool gestorPassaDesligado = false)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpContext.GetEstabelecimentoId() is not Guid loja || HttpContext.GetUserId() is not int usuario)
            return Unauthorized(ApiResponse<object>.Fail("Entre de novo.", "NAO_AUTENTICADO"));
        var ct = HttpContext.RequestAborted;
        try
        {
            var funcao = await nucleo.FuncaoDoUsuarioAsync(loja, usuario, ct);
            var papel = MesasPermissoes.Papel(HttpContext.TemPermissao, HttpContext.IsSuperAdmin(), HttpContext.GetTipoAcessoEmpresa(), funcao);
            if (!MesasPermissoes.Pode(papel, acao)) throw MesasException.SemPermissao();
            var gestor = papel is MesasPermissoes.PapelDono or MesasPermissoes.PapelGerente;
            if (!(gestorPassaDesligado && gestor) && !await nucleo.LigadoAsync(loja, ct)) throw MesasException.Desligado();
            return Ok(ApiResponse<object>.Ok(await executar(new MesasContexto(loja, usuario, papel!))));
        }
        catch (MesasException ex)
        {
            return StatusCode(ex.Status, ApiResponse<object>.Fail(ex.Message, ex.Codigo, ex.Detalhes));
        }
    }
}
