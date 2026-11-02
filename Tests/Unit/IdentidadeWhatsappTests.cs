using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.IdentidadeWhatsapp;
using APIBack.Model.Gestao;
using APIBack.Repository.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit;

/// <summary>Identidade por WhatsApp: so vinculo verificado autoriza; telefone trocado, conta inativa ou revogacao cortam na hora.</summary>
public class IdentidadeWhatsappTests
{
    private const string TelA = "+5534991480112";
    private const string TelB = "+5534998887777";
    private static readonly Guid Empresa = Guid.NewGuid();
    private static readonly Guid Loja = Guid.NewGuid();

    // ---- fluxo principal

    [Fact]
    public async Task Codigo_chega_so_ao_telefone_e_verifica_quando_o_proprio_confirma()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);

        var view = await c.Servico.IniciarVerificacaoAsync(1, atorUsuarioId: 1);

        Assert.Equal(EstadoVinculoWhatsapp.Pendente, view.Estado);
        var (destino, codigo) = Assert.Single(c.Enviados);
        Assert.Equal(TelA, destino);
        Assert.Matches("^[0-9]{6}$", codigo);
        Assert.Null(await c.Servico.AutorizarAsync(TelA)); // pendente nao autoriza

        var ok = await c.Servico.ConfirmarAsync(1, 1, codigo);

        Assert.Equal(EstadoVinculoWhatsapp.Verificado, ok.Estado);
        Assert.Equal(1, (await c.Servico.AutorizarAsync("(34) 99148-0112"))!.UsuarioId);
        Assert.Null(c.Repo.Vinculos[1].CodigoHash); // codigo nao fica guardado depois de usado
    }

    [Fact]
    public async Task Auditoria_e_resposta_nunca_levam_o_codigo()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        var view = await c.Servico.IniciarVerificacaoAsync(1, 1);
        var codigo = c.Enviados.Single().Codigo;
        await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, "000000" == codigo ? "111111" : "000000"));
        await c.Servico.ConfirmarAsync(1, 1, codigo);

        Assert.DoesNotContain(codigo, view.ToString());
        Assert.All(c.Repo.Auditoria, a =>
        {
            Assert.DoesNotContain(codigo, a.ToString());
            Assert.DoesNotContain(TelA, a.ToString());
        });
        Assert.Contains(c.Repo.Auditoria, a => a.Evento == EventoAuditoriaWhatsapp.Confirmado && a.TelefoneFinal == "0112");
    }

    [Fact]
    public async Task Codigo_usado_nao_serve_de_novo()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Servico.IniciarVerificacaoAsync(1, 1);
        var codigo = c.Enviados.Single().Codigo;
        await c.Servico.ConfirmarAsync(1, 1, codigo);
        await c.Servico.RevogarAsync(1, 1, MotivoRevogacaoWhatsapp.Manual);

        var ex = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, codigo));
        Assert.Equal(422, ex.Status);
    }

    [Fact]
    public async Task Outro_usuario_nem_super_admin_confirma_pelo_dono()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Servico.IniciarVerificacaoAsync(1, atorUsuarioId: 99);

        var ex = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 99, c.Enviados.Single().Codigo));

        Assert.Equal(403, ex.Status);
        Assert.Null(await c.Servico.AutorizarAsync(TelA));
    }

    // ---- token expirado, tentativas e limite de envio

    [Fact]
    public async Task Codigo_expirado_e_recusado_com_a_mesma_resposta_de_codigo_errado()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Servico.IniciarVerificacaoAsync(1, 1);
        c.Avancar(IdentidadeWhatsappRegras.ValidadeCodigo + TimeSpan.FromSeconds(1));

        var expirado = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, c.Enviados.Single().Codigo));
        var errado = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, "123"));

        Assert.Equal((422, "codigo_invalido"), (expirado.Status, expirado.Codigo));
        Assert.Equal((errado.Status, errado.Codigo, errado.Message), (expirado.Status, expirado.Codigo, expirado.Message));
        Assert.Equal(EstadoVinculoWhatsapp.NaoVerificado, (await c.Servico.ObterAsync(1)).Estado);
        Assert.Null(await c.Servico.AutorizarAsync(TelA));
    }

    [Fact]
    public async Task Tentativas_esgotadas_suspendem_e_nem_o_codigo_certo_passa()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Servico.IniciarVerificacaoAsync(1, 1);
        var certo = c.Enviados.Single().Codigo;
        var errado = certo == "000000" ? "111111" : "000000";

        for (var i = 1; i < IdentidadeWhatsappRegras.MaxTentativas; i++)
            Assert.Equal(422, (await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, errado))).Status);
        Assert.Equal(429, (await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, errado))).Status);

        Assert.Equal(429, (await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, certo))).Status);
        Assert.Equal(EstadoVinculoWhatsapp.Suspenso, (await c.Servico.ObterAsync(1)).Estado);
        Assert.Equal(429, (await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.IniciarVerificacaoAsync(1, 1))).Status);

        c.Avancar(IdentidadeWhatsappRegras.Bloqueio);
        await c.Servico.IniciarVerificacaoAsync(1, 1); // passou o bloqueio: pode pedir outro
        Assert.Equal(2, c.Enviados.Count);
    }

    [Fact]
    public async Task Reenvio_respeita_intervalo_e_limite_por_hora()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Servico.IniciarVerificacaoAsync(1, 1);

        var cedo = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.IniciarVerificacaoAsync(1, 1));
        Assert.Equal((429, "aguarde_reenvio"), (cedo.Status, cedo.Codigo));
        Assert.NotNull(cedo.TenteNovamenteEm);

        for (var i = 1; i < IdentidadeWhatsappRegras.MaxEnviosPorHora; i++)
        {
            c.Avancar(IdentidadeWhatsappRegras.IntervaloReenvio);
            await c.Servico.IniciarVerificacaoAsync(1, 1);
        }
        c.Avancar(IdentidadeWhatsappRegras.IntervaloReenvio);
        Assert.Equal("limite_envios", (await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.IniciarVerificacaoAsync(1, 1))).Codigo);
        // Codigo antigo morre quando sai um novo.
        Assert.Equal(422, (await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, c.Enviados[0].Codigo == c.Enviados[^1].Codigo ? "x" : c.Enviados[0].Codigo))).Status);
    }

    [Fact]
    public async Task Falha_no_envio_da_meta_nao_deixa_codigo_valendo()
    {
        var c = new Cenario { FalharEnvio = true };
        c.Usuario(1, TelA);

        var ex = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.IniciarVerificacaoAsync(1, 1));

        Assert.Equal(503, ex.Status);
        Assert.Equal(EstadoVinculoWhatsapp.NaoVerificado, c.Repo.Vinculos[1].Estado);
        Assert.Null(c.Repo.Vinculos[1].CodigoHash);
        Assert.Contains(c.Repo.Auditoria, a => a.Evento == EventoAuditoriaWhatsapp.EnvioFalhou);
    }

    // ---- troca, revogacao, inativo

    [Fact]
    public async Task Troca_de_telefone_corta_na_hora_o_antigo_e_o_novo()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Verificar(1);

        c.Usuario(1, TelB); // gestao alterou o telefone (antes de qualquer revogacao gravada)

        Assert.Null(await c.Servico.AutorizarAsync(TelA));
        Assert.Null(await c.Servico.AutorizarAsync(TelB));
        var view = await c.Servico.ObterAsync(1);
        Assert.Equal(EstadoVinculoWhatsapp.Revogado, view.Estado);
        Assert.Equal(MotivoRevogacaoWhatsapp.TelefoneAlterado, view.Motivo);

        await c.Verificar(1); // reverifica no numero novo
        Assert.Equal(1, (await c.Servico.AutorizarAsync(TelB))!.UsuarioId);
        Assert.Null(await c.Servico.AutorizarAsync(TelA));
    }

    [Fact]
    public async Task Revogado_nao_executa_comando_e_revogar_de_novo_e_idempotente()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Verificar(1);

        await c.Servico.RevogarAsync(1, 7, MotivoRevogacaoWhatsapp.Manual);
        var denovo = await c.Servico.RevogarAsync(1, 7, MotivoRevogacaoWhatsapp.Manual);

        Assert.Null(await c.Servico.AutorizarAsync(TelA));
        Assert.Equal(EstadoVinculoWhatsapp.Revogado, denovo.Estado);
        Assert.Single(c.Repo.Auditoria, a => a.Evento == EventoAuditoriaWhatsapp.Revogado);
        Assert.Equal(DestinoRemetente.Qualificacao, (await c.Servico.ResolverRemetenteAsync(TelA, false)).Destino);
    }

    [Fact]
    public async Task Usuario_inativo_perde_acesso_e_nao_pode_verificar()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Verificar(1);

        c.Usuario(1, TelA, ativo: false);

        Assert.Null(await c.Servico.AutorizarAsync(TelA));
        Assert.Equal(EstadoVinculoWhatsapp.Revogado, (await c.Servico.ObterAsync(1)).Estado);
        var ex = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.IniciarVerificacaoAsync(1, 1));
        Assert.Equal((409, "usuario_inativo"), (ex.Status, ex.Codigo));
    }

    // ---- reaproveitamento e colisao

    [Fact]
    public async Task Telefone_reaproveitado_libera_o_antigo_e_o_novo_dono_verifica()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Verificar(1);
        c.Usuario(1, TelB); // o antigo dono mudou de numero
        c.Usuario(2, TelA); // o numero foi para outra pessoa da equipe

        await c.Verificar(2);

        Assert.Equal(2, (await c.Servico.AutorizarAsync(TelA))!.UsuarioId);
        Assert.Equal(EstadoVinculoWhatsapp.Revogado, c.Repo.Vinculos[1].Estado);
        Assert.Equal(MotivoRevogacaoWhatsapp.TelefoneAlterado, c.Repo.Vinculos[1].Motivo);
    }

    [Fact]
    public async Task Telefone_de_duas_pessoas_ativas_nao_verifica_e_nao_diz_de_quem_e()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        c.Usuario(2, TelA);

        var ex = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.IniciarVerificacaoAsync(2, 2));

        Assert.Equal((409, "telefone_indisponivel"), (ex.Status, ex.Codigo));
        Assert.DoesNotContain("1", ex.Message);
        Assert.Empty(c.Enviados);
    }

    [Fact]
    public async Task Colisao_com_vinculo_ainda_valido_de_outro_usuario_e_409()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Verificar(1);
        // Cadastro ruim: o telefone de 1 foi copiado para 2, e 1 continua dono.
        c.Repo.Usuarios[2] = new UsuarioIdentidade(2, TelA, true);
        c.Repo.ForaDaListaPorTelefone.Add(2); // simula leitura antes do cadastro de 2 aparecer na busca por telefone

        var ex = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.IniciarVerificacaoAsync(2, 2));

        Assert.Equal(409, ex.Status);
        Assert.Equal(1, (await c.Servico.AutorizarAsync(TelA))!.UsuarioId);
    }

    [Fact]
    public async Task Gravacao_concorrente_vira_409_sem_revelar_o_resultado()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Servico.IniciarVerificacaoAsync(1, 1);
        c.Repo.ProximaGravacaoPerde = true;

        var ex = await Assert.ThrowsAsync<IdentidadeWhatsappException>(() => c.Servico.ConfirmarAsync(1, 1, c.Enviados.Single().Codigo));

        Assert.Equal((409, "conflito"), (ex.Status, ex.Codigo));
        Assert.Null(await c.Servico.AutorizarAsync(TelA));
    }

    // ---- precedencia e permissoes

    [Fact]
    public async Task Cliente_externo_e_funcionario_nao_verificado_nunca_vao_ao_assistente()
    {
        var c = new Cenario();
        c.Usuario(1, TelA); // da equipe, sem verificar
        c.Repo.ContatosCrm.Add(TelB);

        var naoVerificado = await c.Servico.ResolverRemetenteAsync(TelA, false);
        var cliente = await c.Servico.ResolverRemetenteAsync(TelB, false);
        var desconhecido = await c.Servico.ResolverRemetenteAsync("+5511912345678", false);

        Assert.Equal((DestinoRemetente.Qualificacao, (int?)null, true), (naoVerificado.Destino, naoVerificado.UsuarioId, naoVerificado.InternoNaoVerificado));
        Assert.Equal((DestinoRemetente.AtendimentoPublico, (int?)null), (cliente.Destino, cliente.UsuarioId));
        Assert.Equal(DestinoRemetente.Qualificacao, desconhecido.Destino);
        Assert.Null(await c.Servico.AutorizarAsync(TelB));
    }

    [Fact]
    public async Task Funcionario_que_tambem_e_contato_confirma_o_papel_e_simulador_some_com_a_identidade()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        await c.Verificar(1);

        Assert.Equal(DestinoRemetente.AssistenteInterno, (await c.Servico.ResolverRemetenteAsync(TelA, false)).Destino);

        c.Repo.ContatosCrm.Add(TelA);
        var ambiguo = await c.Servico.ResolverRemetenteAsync(TelA, false);
        var simulador = await c.Servico.ResolverRemetenteAsync(TelA, simulacaoExterna: true);

        Assert.Equal((DestinoRemetente.ConfirmarPapel, (int?)1), (ambiguo.Destino, ambiguo.UsuarioId));
        Assert.Equal((DestinoRemetente.AtendimentoPublico, (int?)null, true), (simulador.Destino, simulador.UsuarioId, simulador.Simulacao));
    }

    [Fact]
    public async Task Autorizacao_usa_permissoes_reais_e_nunca_super_admin()
    {
        var c = new Cenario();
        c.Usuario(1, TelA);
        c.Gestao.Setup(g => g.ObterUsuarioEmpresasAsync(1)).ReturnsAsync(new[]
        {
            new GestaoUsuarioEmpresaRow { EmpresaId = Empresa, TipoAcesso = "super_admin", Status = "ativo" },
        });
        c.Gestao.Setup(g => g.ObterUsuarioEstabelecimentosAsync(1)).ReturnsAsync(new[]
        {
            new GestaoUsuarioEstabelecimentoRow { EmpresaId = Empresa, EstabelecimentoId = Loja, TipoAcesso = "atendente", Status = "ativo" },
            new GestaoUsuarioEstabelecimentoRow { EmpresaId = Empresa, EstabelecimentoId = Guid.NewGuid(), TipoAcesso = "gerente_estabelecimento", Status = "removido", Ativo = false },
        });
        c.Gestao.Setup(g => g.ListarPermissoesPadraoPorTipoAsync(It.IsAny<IReadOnlyCollection<string>>()))
            .ReturnsAsync(new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["atendente"] = new(StringComparer.OrdinalIgnoreCase) { ["Pedidos"] = new List<string> { "visualizar" } },
            });
        await c.Verificar(1);

        var aut = (await c.Servico.AutorizarAsync(TelA))!;

        var escopo = Assert.Single(aut.Escopos);
        Assert.Equal(Loja, escopo.EstabelecimentoId);
        Assert.True(aut.TemPermissao(Empresa, Loja, "Pedidos", "visualizar"));
        Assert.False(aut.TemPermissao(Empresa, Loja, "Pedidos", "editar"));
        Assert.False(aut.TemPermissao(Empresa, Guid.NewGuid(), "Pedidos", "visualizar"));
        Assert.DoesNotContain(aut.Escopos, e => e.TipoAcesso == "super_admin");
    }

    [Fact]
    public void Codigo_e_aleatorio_com_seis_digitos_e_hash_preso_ao_telefone()
    {
        var codigos = Enumerable.Range(0, 50).Select(_ => IdentidadeWhatsappRegras.GerarCodigo()).ToList();
        Assert.All(codigos, cod => Assert.Matches("^[0-9]{6}$", cod));
        Assert.True(codigos.Distinct().Count() > 40);

        var chave = IdentidadeWhatsappRegras.DerivarChave(new string('s', 40));
        var id = Guid.NewGuid();
        Assert.NotEqual(IdentidadeWhatsappRegras.HashCodigo(chave, id, TelA, "123456"), IdentidadeWhatsappRegras.HashCodigo(chave, id, TelB, "123456"));
        Assert.NotEqual(IdentidadeWhatsappRegras.HashCodigo(chave, id, TelA, "123456"), IdentidadeWhatsappRegras.HashCodigo(chave, Guid.NewGuid(), TelA, "123456"));
    }

    // ---- infraestrutura do teste

    private sealed class Cenario
    {
        private DateTime _agora = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

        public Cenario()
        {
            Gestao.Setup(g => g.ObterUsuarioEmpresasAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<GestaoUsuarioEmpresaRow>());
            Gestao.Setup(g => g.ObterUsuarioEstabelecimentosAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<GestaoUsuarioEstabelecimentoRow>());
            var enviador = new Mock<IIdentidadeWhatsappEnviador>();
            enviador.Setup(e => e.EnviarCodigoAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string tel, string cod) =>
                {
                    if (FalharEnvio) throw new IdentidadeWhatsappException(503, "envio_falhou", "falhou");
                    Enviados.Add((tel, cod));
                    return Task.CompletedTask;
                });
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["IdentidadeWhatsapp:Segredo"] = new string('k', 48) })
                .Build();
            Servico = new IdentidadeWhatsappService(Repo, enviador.Object, Gestao.Object, config, NullLogger<IdentidadeWhatsappService>.Instance)
            {
                Agora = () => _agora
            };
        }

        public FakeRepo Repo { get; } = new();
        public Mock<IGestaoRepository> Gestao { get; } = new();
        public IdentidadeWhatsappService Servico { get; }
        public List<(string Telefone, string Codigo)> Enviados { get; } = new();
        public bool FalharEnvio { get; set; }

        public void Usuario(int id, string? telefone, bool ativo = true) => Repo.Usuarios[id] = new UsuarioIdentidade(id, telefone, ativo);

        public void Avancar(TimeSpan t) => _agora += t;

        public async Task Verificar(int id)
        {
            Avancar(IdentidadeWhatsappRegras.IntervaloReenvio);
            await Servico.IniciarVerificacaoAsync(id, id);
            await Servico.ConfirmarAsync(id, id, Enviados[^1].Codigo);
        }
    }

    /// <summary>Em memoria, com as mesmas garantias do banco: versao otimista e telefone unico entre pendente/verificado.</summary>
    private sealed class FakeRepo : IIdentidadeWhatsappRepository
    {
        public Dictionary<int, UsuarioIdentidade> Usuarios { get; } = new();
        public Dictionary<int, VinculoWhatsapp> Vinculos { get; } = new();
        public List<AuditoriaWhatsapp> Auditoria { get; } = new();
        public HashSet<string> ContatosCrm { get; } = new();
        public HashSet<int> ForaDaListaPorTelefone { get; } = new();
        public bool ProximaGravacaoPerde { get; set; }

        public Task<UsuarioIdentidade?> ObterUsuarioAsync(int usuarioId) => Task.FromResult(Usuarios.GetValueOrDefault(usuarioId));

        public Task<IReadOnlyList<UsuarioIdentidade>> ListarUsuariosPorTelefoneAsync(string telefoneE164) =>
            Task.FromResult<IReadOnlyList<UsuarioIdentidade>>(Usuarios.Values
                .Where(u => u.Ativo && u.TelefoneE164 == telefoneE164 && !ForaDaListaPorTelefone.Contains(u.Id)).ToList());

        public Task<VinculoWhatsapp?> ObterVinculoAsync(int usuarioId) => Task.FromResult(Copia(Vinculos.GetValueOrDefault(usuarioId)));

        public Task<IReadOnlyList<VinculoWhatsapp>> ListarVinculosAsync(IReadOnlyCollection<int> usuarioIds) =>
            Task.FromResult<IReadOnlyList<VinculoWhatsapp>>(Vinculos.Values.Where(v => usuarioIds.Contains(v.UsuarioId)).Select(v => Copia(v)!).ToList());

        public Task<VinculoWhatsapp?> ObterVinculoAtivoPorTelefoneAsync(string telefoneE164) =>
            Task.FromResult(Copia(Vinculos.Values.FirstOrDefault(v => v.TelefoneE164 == telefoneE164 && Ativo(v))));

        public Task<bool> ExisteContatoCrmAsync(string telefoneE164) => Task.FromResult(ContatosCrm.Contains(telefoneE164));

        public Task<bool> SalvarAsync(VinculoWhatsapp v, long versaoEsperada)
        {
            if (ProximaGravacaoPerde)
            {
                ProximaGravacaoPerde = false;
                return Task.FromResult(false);
            }
            var atual = Vinculos.GetValueOrDefault(v.UsuarioId);
            if ((atual?.Versao ?? 0) != versaoEsperada) return Task.FromResult(false);
            if (Ativo(v) && Vinculos.Values.Any(o => o.UsuarioId != v.UsuarioId && o.TelefoneE164 == v.TelefoneE164 && Ativo(o)))
                throw IdentidadeWhatsappService.TelefoneIndisponivel();
            v.Versao = versaoEsperada + 1;
            Vinculos[v.UsuarioId] = Copia(v)!;
            return Task.FromResult(true);
        }

        public Task AuditarAsync(AuditoriaWhatsapp auditoria)
        {
            Auditoria.Add(auditoria);
            return Task.CompletedTask;
        }

        private static bool Ativo(VinculoWhatsapp v) => v.Estado is EstadoVinculoWhatsapp.Pendente or EstadoVinculoWhatsapp.Verificado;

        private static VinculoWhatsapp? Copia(VinculoWhatsapp? v) => v == null ? null : new VinculoWhatsapp
        {
            Id = v.Id, UsuarioId = v.UsuarioId, TelefoneE164 = v.TelefoneE164, Estado = v.Estado, CodigoHash = v.CodigoHash?.ToArray(),
            CodigoExpiraEm = v.CodigoExpiraEm, Tentativas = v.Tentativas, EnviosJanela = v.EnviosJanela, JanelaEnvioInicio = v.JanelaEnvioInicio,
            UltimoEnvioEm = v.UltimoEnvioEm, BloqueadoAte = v.BloqueadoAte, VerificadoEm = v.VerificadoEm, RevogadoEm = v.RevogadoEm,
            Motivo = v.Motivo, Versao = v.Versao
        };
    }
}
