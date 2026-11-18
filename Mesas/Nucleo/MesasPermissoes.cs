using APIBack.Security;

namespace APIBack.Mesas.Nucleo;

/// <summary>
/// Papéis e permissões do módulo Mesas (CONTRATO-API §6). Regras puras, sem banco, para testar.
/// Dono e gerente vêm do acesso ao painel (permissão <c>Mesas:configurar</c>, que o catálogo dá a quem gerencia a loja);
/// garçom, caixa, cozinha e bar vêm de <c>gestao_pessoas.funcao_codigo</c>.
/// </summary>
public static class MesasPermissoes
{
    public const string Modulo = "Mesas";

    public const string Ver = "ver", Operar = "operar", Cozinha = "cozinha", Caixa = "caixa",
        Gerir = "gerir", Configurar = "configurar", Reservas = "reservas";

    /// <summary>Ações do módulo, na ordem do contrato. Entram no catálogo de permissões (<c>EstablishmentPermissions.Catalog</c>).</summary>
    public static readonly string[] Acoes = { Ver, Operar, Cozinha, Caixa, Gerir, Configurar, Reservas };

    public const string PapelDono = "dono", PapelGerente = "gerente", PapelCaixa = "caixa",
        PapelGarcom = "garcom", PapelCozinha = "cozinha";

    /// <summary>Funções canônicas gravadas em <c>gestao_pessoas.funcao_codigo</c>.</summary>
    public static readonly string[] Funcoes = { "garcom", "gerente_salao", "cozinha", "bar", "caixa" };

    public static bool FuncaoValida(string? funcao) => funcao is not null && Funcoes.Contains(funcao, StringComparer.Ordinal);

    /// <summary>Papel na loja. Null = não é da equipe do salão.</summary>
    public static string? Papel(Func<string, string, bool> tem, bool superAdmin, string? tipoAcessoEmpresa, string? funcaoCodigo)
    {
        if (superAdmin || RoleCatalog.Normalize(tipoAcessoEmpresa) == "dono") return PapelDono;
        if (tem(Modulo, Configurar) || funcaoCodigo == "gerente_salao") return PapelGerente;
        return funcaoCodigo switch
        {
            "caixa" => PapelCaixa,
            "garcom" => PapelGarcom,
            "cozinha" or "bar" => PapelCozinha,
            _ => tem(Modulo, Caixa) ? PapelCaixa : null
        };
    }

    /// <summary>Tabela §6.2. Garçom em <c>caixa</c> só recebe (a autonomia <c>receber</c> é conferida no comando).</summary>
    public static bool Pode(string? papel, string acao) => (papel, acao) switch
    {
        (null, _) => false,
        (PapelDono or PapelGerente, _) => Acoes.Contains(acao, StringComparer.Ordinal),
        (_, Ver) => true,
        (PapelCaixa or PapelGarcom, Operar or Caixa or Reservas) => true,
        (PapelCozinha, Cozinha) => true,
        _ => false
    };

    /// <summary>Ligar ou desligar o módulo na loja é só do dono.</summary>
    public static bool PodeLigar(string? papel) => papel == PapelDono;
}
