using System;
using System.Collections.Generic;
using APIBack.Acompanhamento.Saude;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Sugestao de fim de teste: a falha nossa pesa a favor da loja (prompt 04, sessao 3).</summary>
    public sealed class FimDeTesteSugestaoTests
    {
        private static FalhaNoTesteDto Falha(string causa = "WhatsApp fora 4 dias", string executor = "zippy") =>
            new(Guid.NewGuid(), "canal_meta", DateTimeOffset.UtcNow.AddDays(-5), null, causa, executor, "aberta");

        private static readonly IReadOnlyList<FalhaNoTesteDto> Nenhuma = Array.Empty<FalhaNoTesteDto>();

        [Fact]
        public void Falha_nossa_com_pouco_uso_no_teste_sugere_prorrogar()
        {
            var s = SaudeService.SugerirFimDeTeste(true, false, false, 6, 30, new[] { Falha() });
            Assert.Equal("prorrogar_teste", s!.Acao);
            Assert.Contains("1 falha nossa", s.Motivo);
        }

        [Fact]
        public void Falha_nossa_com_pouco_uso_depois_do_fim_sugere_dias_sem_cobrar()
        {
            var s = SaudeService.SugerirFimDeTeste(false, true, false, 6, 30, new[] { Falha(), Falha("iFood fora") });
            Assert.Equal("dar_dias_sem_cobrar", s!.Acao);
            Assert.Contains("2 falhas nossas", s.Motivo);
        }

        [Fact]
        public void Falha_nossa_nao_impede_propor_plano_para_quem_usa_bem()
        {
            var s = SaudeService.SugerirFimDeTeste(true, false, false, 41, 30, new[] { Falha() });
            Assert.Equal("propor_plano", s!.Acao);
        }

        [Fact]
        public void Sem_falha_nossa_e_pouco_uso_sugere_conversar_ou_investigar()
        {
            Assert.Equal("conversar_sobre_uso", SaudeService.SugerirFimDeTeste(true, false, false, 6, 30, Nenhuma)!.Acao);
            Assert.Equal("investigar_motivo", SaudeService.SugerirFimDeTeste(true, false, false, 0, 30, Nenhuma)!.Acao);
        }

        [Fact]
        public void Plano_pago_ativa_sozinho_e_loja_ja_operando_sem_teste_nao_tem_sugestao()
        {
            Assert.Equal("ativar_ao_terminar", SaudeService.SugerirFimDeTeste(true, false, true, 3, 30, new[] { Falha() })!.Acao);
            Assert.Null(SaudeService.SugerirFimDeTeste(false, false, false, 50, 30, Nenhuma));
            Assert.Null(SaudeService.SugerirFimDeTeste(false, true, true, 50, 30, Nenhuma));
        }
    }
}
