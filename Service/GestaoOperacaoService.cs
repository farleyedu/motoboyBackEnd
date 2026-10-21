using APIBack.DTOs.Gestao;
using APIBack.Repository.Interface;

namespace APIBack.Service;

public interface IGestaoOperacaoService
{
    Task<VisaoGeralOperacaoDto> VisaoGeralAsync(int userId, Guid empresaId, string? companyRole, bool superAdmin, bool podeVerIfood, CancellationToken ct = default);
}

/// <summary>
/// Painel de operacao entre lojas: monta, numa leitura agrupada, os numeros que o operador hoje precisa abrir loja por loja.
/// Gestor da empresa (dono, gerente da empresa) e super admin veem todas as lojas operacionais; os demais, so as que tem vinculadas.
/// </summary>
public sealed class GestaoOperacaoService(IGestaoOperacaoRepository repository) : IGestaoOperacaoService
{
    public static bool VeTodasAsLojas(string? companyRole, bool superAdmin) =>
        superAdmin
        || string.Equals(companyRole, "dono", StringComparison.OrdinalIgnoreCase)
        || string.Equals(companyRole, "gerente_empresa", StringComparison.OrdinalIgnoreCase);

    // Loja em teste tem tudo liberado (como TemModuloAsync); fora dele vale a lista de modulos da loja.
    private static bool Tem(OperacaoLojaRow loja, string modulo) =>
        loja.Situacao == SituacoesComerciais.Teste || loja.Modulos.Contains(modulo, StringComparer.OrdinalIgnoreCase);

    public async Task<VisaoGeralOperacaoDto> VisaoGeralAsync(int userId, Guid empresaId, string? companyRole, bool superAdmin, bool podeVerIfood, CancellationToken ct = default)
    {
        var lojas = await repository.ListarLojasAsync(empresaId, userId, VeTodasAsLojas(companyRole, superAdmin), ct);
        // Loja bloqueada aparece (o operador precisa saber), mas sem contadores: nada opera nela.
        var operando = lojas.Where(l => l.Situacao != SituacoesComerciais.Bloqueado).Select(l => l.Id).ToArray();
        var contadores = (await repository.ContadoresAsync(operando, ct)).ToDictionary(c => c.EstabelecimentoId);
        var ifoodLojas = podeVerIfood ? lojas.Where(l => l.Situacao != SituacoesComerciais.Bloqueado && Tem(l, "IFOOD")).Select(l => l.Id).ToArray() : Array.Empty<Guid>();
        var alertas = (await repository.AlertasIfoodAsync(ifoodLojas, ct)).ToDictionary(a => a.EstabelecimentoId);

        var result = new VisaoGeralOperacaoDto { EmpresaId = empresaId, GeradoEmUtc = DateTimeOffset.UtcNow };
        foreach (var loja in lojas)
        {
            var dto = new LojaOperacaoDto
            {
                EstabelecimentoId = loja.Id, Nome = loja.Nome, SituacaoComercial = loja.Situacao,
                TemDelivery = Tem(loja, "DELIVERY"), TemIfood = Tem(loja, "IFOOD")
            };
            if (contadores.TryGetValue(loja.Id, out var c))
            {
                if (Tem(loja, "PEDIDOS")) { dto.PedidosPendentes = c.Pendentes; dto.PedidosEmRota = c.EmRota; }
                if (dto.TemDelivery) { dto.MotoboysOnline = c.MotoboysOnline; dto.TransferenciasPendentes = c.TransferenciasPendentes; }
            }
            if (alertas.TryGetValue(loja.Id, out var a)) { dto.IfoodAlertasCriticos = a.Criticos; dto.IfoodAlertasAviso = a.Avisos; }
            result.Lojas.Add(dto);
        }
        return result;
    }
}
