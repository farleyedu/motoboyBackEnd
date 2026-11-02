using APIBack.Automation.Helpers;
using APIBack.DTOs.CRM;
using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>CRM, Fase 0: o fechamento nunca liga por nome identico, telefone em E.164, lancamento manual nunca "pago".</summary>
public class CrmFase0Tests
{
    private static readonly Guid Bistro = Guid.NewGuid();
    private static readonly Guid OutroBistro = Guid.NewGuid();
    private static readonly Guid Pizzaria = Guid.NewGuid();

    private static readonly CrmEmpresaExistente[] Empresas =
    {
        new(Bistro, "Bistrô Alecrim", "11.222.333/0001-81"),
        new(OutroBistro, "Bistro Alecrim Ltda", null),
        new(Pizzaria, "Pizzaria do Zé", "99.888.777/0001-00"),
    };

    // ---- 1. associacao no fechamento

    [Fact]
    public void Nome_identico_nao_liga_sozinho_so_vira_candidato_para_o_operador_confirmar()
    {
        var candidatos = CrmAssociacaoRegras.Candidatos(null, "Pizzaria do Zé", Empresas);

        var candidato = Assert.Single(candidatos);
        Assert.Equal(CrmAssociacaoRegras.MotivoNomeParecido, candidato.Motivo);
        // Sem empresa confirmada, o fechamento cria empresa nova (nao reaproveita a de nome igual).
        Assert.Null(CrmAssociacaoRegras.ValidarEscolha(candidatos, null));
    }

    [Fact]
    public void Nome_parecido_sem_acento_e_sem_razao_social_so_sugere()
    {
        var candidatos = CrmAssociacaoRegras.Candidatos(null, "BISTRO ALECRIM", Empresas);

        Assert.Equal(new[] { Bistro, OutroBistro }.OrderBy(i => i), candidatos.Select(c => c.EmpresaId).OrderBy(i => i));
        Assert.All(candidatos, c => Assert.Equal(CrmAssociacaoRegras.MotivoNomeParecido, c.Motivo));
    }

    [Fact]
    public void Nomes_diferentes_nao_viram_candidato()
    {
        Assert.Empty(CrmAssociacaoRegras.Candidatos(null, "Hamburgueria Central", Empresas));
    }

    [Fact]
    public void Cnpj_igual_e_a_mesma_empresa_e_exige_confirmar_essa()
    {
        var candidatos = CrmAssociacaoRegras.Candidatos("11222333000181", "Outro nome qualquer", Empresas);

        var candidato = Assert.Single(candidatos);
        Assert.Equal(Bistro, candidato.EmpresaId);
        Assert.Equal(CrmAssociacaoRegras.MotivoCnpj, candidato.Motivo);
        Assert.NotNull(CrmAssociacaoRegras.ValidarEscolha(candidatos, null));
        Assert.NotNull(CrmAssociacaoRegras.ValidarEscolha(candidatos, Pizzaria));
        Assert.Null(CrmAssociacaoRegras.ValidarEscolha(candidatos, Bistro));
    }

    [Fact]
    public void Operador_pode_confirmar_um_candidato_de_nome_parecido()
    {
        var candidatos = CrmAssociacaoRegras.Candidatos(null, "Bistro Alecrim", Empresas);
        Assert.Null(CrmAssociacaoRegras.ValidarEscolha(candidatos, OutroBistro));
    }

    [Fact]
    public void Candidatos_ordenam_cnpj_antes_de_nome_parecido()
    {
        var candidatos = CrmAssociacaoRegras.Candidatos("11222333000181", "Bistro Alecrim", Empresas);

        Assert.Equal(CrmAssociacaoRegras.MotivoCnpj, candidatos[0].Motivo);
        Assert.Equal(Bistro, candidatos[0].EmpresaId);
        Assert.Equal(OutroBistro, Assert.Single(candidatos.Skip(1)).EmpresaId);
    }

    [Fact]
    public void Repositorio_nao_procura_empresa_por_nome_nem_pega_o_primeiro_estabelecimento()
    {
        var repo = File.ReadAllText(ProjectFile("Repository", "CrmRepository.cs"));

        Assert.DoesNotContain("lower(btrim(nome_fantasia)) = lower(btrim(@NomeEmpresa))", repo, StringComparison.Ordinal);
        Assert.DoesNotContain("lower(btrim(o.nome_empresa)) = lower(btrim(alvo.nome_empresa))", repo, StringComparison.Ordinal);
        Assert.DoesNotContain("ObterEmpresaExistenteAsync", repo, StringComparison.Ordinal);
        Assert.DoesNotContain("ObterEstabelecimentoExistenteAsync", repo, StringComparison.Ordinal);
    }

    // ---- 2. telefone do usuario em E.164

    [Theory]
    [InlineData("(34) 99148-0112", "+5534991480112")]
    [InlineData("34991480112", "+5534991480112")]
    [InlineData("+55 34 99148-0112", "+5534991480112")]
    [InlineData("5534991480112", "+5534991480112")]
    [InlineData("(11) 3456-7890", "+551134567890")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Telefone_valido_vira_e164_e_vazio_vira_nulo(string? bruto, string? esperado)
    {
        Assert.True(TelefoneHelper.TryNormalizarE164(bruto, out var e164));
        Assert.Equal(esperado, e164);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("991480112")]
    [InlineData("(34) 89148-0112")]
    [InlineData("(05) 99148-0112")]
    [InlineData("+1 415 555 0100")]
    public void Telefone_invalido_e_recusado(string bruto)
    {
        Assert.False(TelefoneHelper.TryNormalizarE164(bruto, out var e164));
        Assert.Null(e164);
    }

    [Fact]
    public void Usuario_grava_e_le_o_telefone_em_e164()
    {
        var servico = File.ReadAllText(ProjectFile("Service", "GestaoService.cs"));
        var repo = File.ReadAllText(ProjectFile("Repository", "GestaoRepository.cs"));

        Assert.Contains("TryNormalizarE164(request.Telefone", servico, StringComparison.Ordinal);
        Assert.Contains("Telefone = summary.TelefoneE164", servico, StringComparison.Ordinal);
        Assert.DoesNotContain("Telefone = null,", servico[servico.IndexOf("new GestaoUsuarioDto", StringComparison.Ordinal)..][..400], StringComparison.Ordinal);
        Assert.Contains("u.telefone_e164", repo, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO usuario (nome, email, telefone_e164", repo, StringComparison.Ordinal);
        Assert.Contains("telefone_e164 = CASE WHEN @TelefoneInformado", repo, StringComparison.Ordinal);
    }

    // ---- 3. lancamento manual nunca vira "pago"

    [Theory]
    [InlineData("pago", "registrado_manual")]
    [InlineData("parcial", "parcial_manual")]
    [InlineData("pendente", "pendente")]
    [InlineData("cancelado", "cancelado")]
    public void Lancamento_do_crm_e_rotulado_como_manual(string statusBanco, string esperado)
    {
        Assert.Equal(esperado, CrmService.RotuloManual(statusBanco));
        Assert.NotEqual("pago", CrmService.RotuloManual(statusBanco));
    }

    [Fact]
    public void Lancamento_do_crm_sai_com_origem_manual()
    {
        Assert.Equal("manual", new CrmLancamentoDto().Origem);
    }

    [Fact]
    public void Dinheiro_e_atraso_da_gestao_vem_da_assinatura_e_nao_do_lancamento_manual()
    {
        var gestao = File.ReadAllText(ProjectFile("Service", "GestaoCentralService.cs"));
        var carteira = gestao[gestao.IndexOf("public async Task<IReadOnlyList<CarteiraLojaDto>> CarteiraAsync", StringComparison.Ordinal)..];
        carteira = carteira[..carteira.IndexOf("public async Task<DinheiroMesDto> DinheiroAsync", StringComparison.Ordinal)];
        var dinheiro = gestao[gestao.IndexOf("public async Task<DinheiroMesDto> DinheiroAsync", StringComparison.Ordinal)..];
        dinheiro = dinheiro[..dinheiro.IndexOf("private const string ContaSelect", StringComparison.Ordinal)];

        Assert.Contains("comercial_cobranca", carteira, StringComparison.Ordinal);
        Assert.DoesNotContain("crm_lancamento", carteira, StringComparison.Ordinal);
        Assert.Contains("comercial_cobranca", dinheiro, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM crm_lancamento", dinheiro, StringComparison.Ordinal);
    }

    // ---- 4. responsaveis reais

    [Fact]
    public void Crm_nao_valida_mais_responsavel_fixo_farley_socio()
    {
        var servico = File.ReadAllText(ProjectFile("Service", "CrmService.cs"));
        var repo = File.ReadAllText(ProjectFile("Repository", "CrmRepository.cs"));

        Assert.DoesNotContain("\"farley\", \"socio\"", servico, StringComparison.Ordinal);
        Assert.DoesNotContain("crm_responsavel", repo, StringComparison.Ordinal);
        Assert.DoesNotContain("MatchResponsavel", repo, StringComparison.Ordinal);
        Assert.Contains("responsavel_usuario_id", repo, StringComparison.Ordinal);
    }

    // ---- migracao

    [Fact]
    public void Migracao_e_aditiva_e_migra_farley_e_socio()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261124_01_crm_fase0.sql"));

        Assert.Contains("ADD COLUMN IF NOT EXISTS telefone_e164", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS responsavel_usuario_id", sql, StringComparison.Ordinal);
        Assert.Contains("responsavel::text = 'farley'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProjectFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "APIBack.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }
}
